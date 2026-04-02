module SinkFeeds

open DomainModel
open Serialisation

let getSinkFeedKey (sinkSetting: SinkSetting) = $"{sinkSetting.SinkSlug}.sink.json"

let isEquivalent (item: RssItem) (derivedItemReference: DerivedItemReference) =
    item.Title = derivedItemReference.Title
    && item.Guid = derivedItemReference.Guid
    && item.Link = derivedItemReference.Link

// The point of the derivedItemReference is that the final item will have content from multiple derived items,
// and you need some reference to see this
let getFreshDerivedItems (sinkSetting: SinkSetting) (derivedItems: DerivedBatch array) =
    task {
        let feedKey = getSinkFeedKey sinkSetting
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

            | None -> Array.truncate sinkSetting.SourceItemsPerPublish flattenedDerivedItems
    }

//