module SinkFeeds

open DerivedFeeds
open DomainModel
open Serialisation
open ObjectStorage
open System.Globalization
open Giraffe.ViewEngine
open System.Threading.Tasks
open System
open Microsoft.Extensions.Logging

let getFeedKey (sinkSetting: SinkSetting) = $"{sinkSetting.SinkSlug}.sink.json"

let getPossibleFeedKey possibleSlug = $"{possibleSlug}.sink.json"

let toTitleCase = CultureInfo.CurrentCulture.TextInfo.ToTitleCase 

let isEquivalent (item: RssItem) (derivedItemReference: DerivedItemReference) =
    item.Title = derivedItemReference.Title
    && item.Guid = derivedItemReference.Guid
    && item.Link = derivedItemReference.Link

let getDerivedItemsWithSlug (storage: ObjectStorageService) (sinkSetting: SinkSetting) =

    let getDerivedTuple sourceSlug =
        task {
            let! object = getDerivedFeedKeyFromSlug sourceSlug |> storage.GetObject
            return sourceSlug, object
        }

    task {
        let! maybeDerivedS3Objects = sinkSetting.SourceSlugs |> Array.map getDerivedTuple |> Task.WhenAll

        return
            maybeDerivedS3Objects
            |> Array.map (fun slugMaybeObjectPair ->
                snd slugMaybeObjectPair
                |> Option.map (fun s3Object -> fst slugMaybeObjectPair, s3Object))
            |> Array.map Option.toArray
            |> Array.concat
            |> Array.map (fun slugObjectPair ->
                snd slugObjectPair
                |> _.Content
                |> deserialise<DerivedFeed>
                |> fun derivedFeed -> derivedFeed.Batches |> Array.map (fun batch -> fst slugObjectPair, batch))
            |> Array.concat
    }

let getFreshDerivedItems
    maybeSinkS3Object
    (sinkSetting: SinkSetting)
    (derivedItemsWithSlug: (SourceSlug * DerivedBatch) array)
    =
    task {
        let flattenedDerivedItems =
            derivedItemsWithSlug
            |> Array.map (fun derivedPair ->
                snd derivedPair
                |> _.BatchItems
                |> Array.map (fun batch -> fst derivedPair, batch))
            |> Array.concat
            |> Array.filter (fun pair -> snd pair |> _.Result |> Option.isSome)

        return
            match maybeSinkS3Object with
            | Some sinkS3Object ->
                let sinkFeed = deserialise<SinkFeed> sinkS3Object.Content

                flattenedDerivedItems
                |> Array.filter (fun derivedPair ->
                    sinkFeed.Items
                    |> Array.map _.DerivedItemReferences
                    |> Array.concat
                    |> Array.tryFind (snd derivedPair |> _.Item |> isEquivalent)
                    |> Option.isNone)
                |> Array.truncate sinkSetting.SourceItemsPerPublish

            | None -> flattenedDerivedItems
    }

let getToPublish (derivedPairs: (SourceSlug * DerivedItem) array) sinkSetting =
    let publishDate = DateTimeOffset.UtcNow

    let content =
        div
            []
            [ yield!
                  derivedPairs
                  |> Array.filter (fun pair ->
                      let derivedItem = snd pair
                      derivedItem.Included && derivedItem.Result.IsSome)
                  |> Array.map (fun pair ->
                      let derivedItem = snd pair
                      let sourceSlug = fst pair
                      div [] [ h2 [] [ str $"{sourceSlug}: {derivedItem.Item.Title}" ]; p [] [ str derivedItem.Result.Value ] ]) ]

    let baseItem =
        { Title = $"{sinkSetting.SinkSlug}: Update {publishDate.Date}"
          Guid = Guid.NewGuid().ToString() |> Some
          Link = None
          Description = RenderView.AsString.htmlNode content
          Content = ""
          PubDate = rfc822Date publishDate |> Some }

    let derivedItemReferences =
        derivedPairs
        |> Array.map (fun derivedPair ->
            let item = snd derivedPair |> _.Item

            { Title = item.Title
              Guid = item.Guid
              Link = item.Link })

    { Item = baseItem
      DerivedItemReferences = derivedItemReferences }


let feedUpdate (logger: ILogger) (storage: ObjectStorageService) (sink: SinkSetting) =
    let update sinkSetting =
        task {
            let! incomingDerivedItemsWithSlug = getDerivedItemsWithSlug storage sinkSetting
            let feedKey = getFeedKey sinkSetting
            let! maybeSinkS3Object = storage.GetObject feedKey
            let! freshDerivedItems = getFreshDerivedItems maybeSinkS3Object sinkSetting incomingDerivedItemsWithSlug

            let batchCount = freshDerivedItems.Length / sinkSetting.SourceItemsPerPublish

            let toPublish =
                freshDerivedItems
                |> Array.truncate (batchCount * sinkSetting.SourceItemsPerPublish)
                |> Array.chunkBySize sinkSetting.SourceItemsPerPublish
                |> Array.map (fun derivedPairs -> getToPublish derivedPairs sinkSetting)

            let pubDate = rfc822Date DateTimeOffset.UtcNow

            match maybeSinkS3Object with
            | Some sinkS3Object ->
                match batchCount with
                | 0 ->
                    logger.LogInformation("No fresh items to add for sink {SinkSlug}", sinkSetting.SinkSlug)
                    return Result.Ok 0
                | x when x > 0 ->
                    let sinkFeed = deserialise<SinkFeed> sinkS3Object.Content

                    let updatedSinkFeed =
                        { Title = sinkFeed.Title
                          Link = sinkFeed.Link
                          PubDate = pubDate
                          Description = sinkFeed.Description
                          Items = Array.append sinkFeed.Items toPublish }

                    let! putResult = storage.PutObject feedKey (serialise updatedSinkFeed) (Some sinkS3Object.ETag)
                    logger.LogInformation("Sink {SinkSlug} added {Count} fresh items", sinkSetting.SinkSlug, x)
                    return putResult |> Result.map (fun _ -> x)
                | _ -> return Result.Error Net.HttpStatusCode.InternalServerError

            | None ->
                let slugLabel = String.Join(", ", Set sinkSetting.SourceSlugs)

                let sinkFeed: SinkFeed =
                    { Title = $"{sinkSetting.SinkSlug}: Summary Sink Feed"
                      Link = $"https://rss-scrape.iainschmitt.com/{sinkSetting.SinkSlug}"
                      PubDate = pubDate
                      Description = $"Summarised feed for {slugLabel}"
                      Items = toPublish }

                let! putResult = storage.PutObject feedKey (serialise sinkFeed) None

                let slugLabel = serialise sinkSetting.SinkSlug |> toTitleCase

                logger.LogInformation("Sink {SinkSlug} created with {Count} items", slugLabel, toPublish.Length)

                return putResult |> Result.map (fun _ -> toPublish.Length)
        }

    storage.RetryHttp 3 (fun () -> update sink)
