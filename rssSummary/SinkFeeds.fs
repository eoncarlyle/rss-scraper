module SinkFeeds

open System.Globalization
open DerivedFeeds
open DomainModel
open Serialisation
open Giraffe.ViewEngine
open System.Threading.Tasks
open System

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

    task { //TODO: include references to origin
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

let getFreshDerivedItems (sinkSetting: SinkSetting) (derivedItemsWithSlug: (SourceSlug * DerivedBatch) array) =
    task {
        let feedKey = getFeedKey sinkSetting
        let! maybeSinkS3Object = ObjectStorage.getS3Object feedKey

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



let feedUpdate (sinkSetting: SinkSetting) =
    task {
        let! incomingDerivedItemsWithSlug = getDerivedItemsWithSlug sinkSetting
        let! freshDerivedItems = getFreshDerivedItems sinkSetting incomingDerivedItemsWithSlug

        let batchCount = freshDerivedItems.Length / sinkSetting.SourceItemsPerPublish

        if batchCount = 0 then
            Console.WriteLine "No fresh items to add for sink ${sinkSetting}"
        else
            let toPublish =
                freshDerivedItems
                |> Array.truncate (batchCount * sinkSetting.SourceItemsPerPublish)
                |> Array.chunkBySize sinkSetting.SourceItemsPerPublish
                |> Array.map (fun derivedPairs ->
                    let publishDate = DateTimeOffset.UtcNow
                    let slugLabel = String.Join(",", Array.map fst derivedPairs |> Set)

                    let content =
                        div
                            []
                            [ yield!
                                  derivedPairs
                                  |> Array.map snd
                                  |> Array.filter _.Included
                                  |> Array.filter _.Result.IsSome
                                  |> Array.map (fun derivedItem ->
                                      div
                                          []
                                          [ h2 [] [ str derivedItem.Item.Title ]
                                            p [] [ str derivedItem.Result.Value ] ]) ]

                    let baseItem =
                        { Title = $"{sinkSetting.SinkSlug}: Update {publishDate.Date}"
                          Guid = Guid.NewGuid.ToString() |> Some
                          Link = None
                          Description = $"Published {publishDate} with feeds ${slugLabel}"
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

                )

            let feedKey = getFeedKey sinkSetting
            let! maybeSinkS3Object = ObjectStorage.getS3Object feedKey

            match maybeSinkS3Object with
            | Some sinkS3Object ->
                let sinkFeed = deserialise<SinkFeed> sinkS3Object.Content

                let updatedSinkFeed =
                    { Title = sinkFeed.Title
                      Link = sinkFeed.Link
                      PubDate = rfc822Date DateTimeOffset.UtcNow
                      Description = sinkFeed.Description
                      Items = Array.append sinkFeed.Items toPublish }

                //return! ObjectStorage.putS3Object feedKey (serialise updatedSinkFeed) (Some sinkS3Object.ETag)
                ()
            | None -> ()
    }
