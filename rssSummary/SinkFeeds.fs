module SinkFeeds

open DerivedFeeds
open DomainModel
open Serialisation
open Giraffe.ViewEngine
open System.Threading.Tasks
open System
open System.IO

let sinkSettings =
    File.ReadAllText "sink-settings.json" |> deserialise<SinkSettings>

let getFeedKey (sinkSetting: SinkSetting) = $"{sinkSetting.SinkSlug}.sink.json"

let isEquivalent (item: RssItem) (derivedItemReference: DerivedItemReference) =
    item.Title = derivedItemReference.Title
    && item.Guid = derivedItemReference.Guid
    && item.Link = derivedItemReference.Link

let getDerivedItemsWithSlug (sinkSetting: SinkSetting) =

    let getDerivedTuple sourceSlug =
        task {
            let! object = getDerivedFeedKeyFromSlug sourceSlug |> ObjectStorage.getS3Object
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

let getFreshDerivedItems maybeSinkS3Object (sinkSetting: SinkSetting) (derivedItemsWithSlug: (SourceSlug * DerivedBatch) array) =
    task {
        let flattenedDerivedItems =
            derivedItemsWithSlug
            |> Array.map (fun derivedPair ->
                snd derivedPair
                |> _.BatchItems
                |> Array.map (fun batch -> fst derivedPair, batch))
            |> Array.concat

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
    let slugLabel = String.Join(", ", Array.map fst derivedPairs |> Set)

    let content =
        div
            []
            [ yield!
                  derivedPairs
                  |> Array.map snd
                  |> Array.filter _.Included
                  |> Array.filter _.Result.IsSome
                  |> Array.map (fun derivedItem ->
                      div [] [ h2 [] [ str derivedItem.Item.Title ]; p [] [ str derivedItem.Result.Value ] ]) ]

    let baseItem =
        { Title = $"{sinkSetting.SinkSlug}: Update {publishDate.Date}"
          Guid = Guid.NewGuid().ToString() |> Some
          Link = None
          Description = $"Published {publishDate} with feeds {slugLabel}"
          Content = RenderView.AsString.htmlDocument content
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


let feedUpdate (sink: SinkSetting) =
    let update sinkSetting =
        task {
            let! incomingDerivedItemsWithSlug = getDerivedItemsWithSlug sinkSetting
            let feedKey = getFeedKey sinkSetting
            let! maybeSinkS3Object = ObjectStorage.getS3Object feedKey
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
                    Console.WriteLine $"No fresh items to add for sink {sinkSetting}" 
                    return Result.Ok 0
                | x when x > 0 ->
                    let sinkFeed = deserialise<SinkFeed> sinkS3Object.Content
                    let updatedSinkFeed =
                        { Title = sinkFeed.Title
                          Link = sinkFeed.Link
                          PubDate = pubDate
                          Description = sinkFeed.Description
                          Items = Array.append sinkFeed.Items toPublish }

                    let! putResult = ObjectStorage.putS3Object feedKey (serialise updatedSinkFeed) (Some sinkS3Object.ETag)
                    Console.WriteLine $"Sink {sinkSetting} added {x} fresh items" 
                    return putResult |> Result.map (fun _ -> x)
                | _ -> return Result.Error Net.HttpStatusCode.InternalServerError
                
            | None ->
                let slugLabel = String.Join(", ", Set sinkSetting.SourceSlugs)
                let sinkFeed: SinkFeed =
                    { Title = $"RSS Summary Service {sinkSetting.SinkSlug} Sink Feed"
                      Link = $"https://rss-scrape.iainschmitt.com/{sinkSetting.SinkSlug}"
                      PubDate = pubDate
                      Description = $"Summarised feed for {slugLabel}"
                      Items = toPublish }

                let! putResult = ObjectStorage.putS3Object feedKey (serialise sinkFeed) None
                Console.WriteLine $"Sink {sinkSetting.SinkSlug} created with {toPublish.Length} items" 
                return putResult |> Result.map (fun _ -> toPublish.Length)
        }
        
    ObjectStorage.retryHttp 3 (fun () -> update sink)
