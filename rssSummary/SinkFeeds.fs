module SinkFeeds

open System.Globalization
open DerivedFeeds
open DomainModel
open Serialisation
open System.Threading.Tasks
open System

let getFeedKey (sinkSetting: SinkSetting) = $"{sinkSetting.SinkSlug}.sink.json"

let isEquivalent (item: RssItem) (derivedItemReference: DerivedItemReference) =
    item.Title = derivedItemReference.Title
    && item.Guid = derivedItemReference.Guid
    && item.Link = derivedItemReference.Link

let getDerivedItems (sinkSetting: SinkSetting) =
    task { //TODO: include references to origin
        let! maybeDerivedS3Objects =
            sinkSetting.SourceSlugs
            |> Array.map getFeedKeyFromSlug
            |> Array.map ObjectStorage.getS3Object
            |> Task.WhenAll

        return
            maybeDerivedS3Objects
            |> Array.map Option.toArray
            |> Array.concat
            |> Array.map _.Content
            |> Array.map deserialise<DerivedFeed>
            |> Array.map _.Batches
            |> Array.concat
    }

// The point of the derivedItemReference is that the final item will have content from multiple derived items,
// and you need some reference to see this
let getFreshDerivedItems (sinkSetting: SinkSetting) (derivedItems: DerivedBatch array) =
    task {
        let feedKey = getFeedKey sinkSetting
        let! maybeSinkS3Object = ObjectStorage.getS3Object feedKey
        let flattenedDerivedItems = derivedItems |> Array.map _.BatchItems |> Array.concat

        return
            match maybeSinkS3Object with
            | Some sinkS3Object ->
                let sinkFeed = deserialise<SinkFeed> sinkS3Object.Content

                flattenedDerivedItems
                |> Array.filter (fun derived ->
                    sinkFeed.Items
                    |> Array.map _.DerivedItemReferences
                    |> Array.concat
                    |> Array.tryFind (isEquivalent derived.Item)
                    |> Option.isNone)
                |> Array.truncate sinkSetting.SourceItemsPerPublish

            | None -> flattenedDerivedItems
    }

let rfc822Date (dto: DateTimeOffset) =
    dto
        .ToUniversalTime()
        .ToString("ddd, dd MMM yyyy HH:mm:ss UTC", CultureInfo.InvariantCulture)

let feedUpdate (sinkSetting: SinkSetting) =
    task {
        let! incomingDerivedItems = getDerivedItems sinkSetting
        let! freshDerivedItems = getFreshDerivedItems sinkSetting incomingDerivedItems

        let batchCount = freshDerivedItems.Length / sinkSetting.SourceItemsPerPublish

        if batchCount = 0 then
            Console.WriteLine "No fresh items to add for sink ${sinkSetting}"
        else
            let toPublish =
                freshDerivedItems
                |> Array.truncate (batchCount * sinkSetting.SourceItemsPerPublish)
                |> Array.chunkBySize sinkSetting.SourceItemsPerPublish
                |> Array.map (fun derivedItems ->
                    let publishDate = DateTimeOffset.UtcNow
                    let slugLabel = String.Join(",", sinkSetting.SourceSlugs)

                    let baseItem =
                        { Title = $"{sinkSetting.SinkSlug}: Update {publishDate.Date}"
                          Guid = Guid.NewGuid.ToString() |> Some
                          Link = None
                          Description = $"Published {publishDate} from ${slugLabel}"
                          Content = "" //TODO actually make this
                          PubDate = Some publishDate }

                    let derivedItemReferences =
                        derivedItems
                        |> Array.map (fun derivedItem ->
                            { Title = derivedItem.Item.Title
                              Guid = derivedItem.Item.Guid
                              Link = derivedItem.Item.Link })


                    { Item = baseItem
                      DerivedItemReferences = derivedItemReferences }

                )

            let feedKey = getFeedKey sinkSetting
            let! maybeSinkS3Object = ObjectStorage.getS3Object feedKey

            match maybeSinkS3Object with
            | Some sinkS3Object -> ()
            | None -> ()
    }
