module DerivedSourceFeeds

open Giraffe.ViewEngine
open FSharp.Data
open System.IO
open Serialisation
open Models

type SourcesConfiguration = XmlProvider<"schema/SourcesConfiguration.xml">

let sourcesConfiguration =
    SourcesConfiguration.Load "schema/SourcesConfiguration.xml"

let getDerivedSourceFeedFileName (source: SourcesConfiguration.Source) =
    $"/Users/iain/code/rss-scraper/rssSummary/{source.SourceSlug}.derived.xml"

let getTempDerivedSourceFeedFileName (source: SourcesConfiguration.Source) =
    $"{getDerivedSourceFeedFileName source}.tmp"

let updateDerivedSourceFeed (source: SourcesConfiguration.Source) (incomingBatch: SourceFeedSummaryRequestBatch) =
    let feedFileName = getDerivedSourceFeedFileName source
    let tempFeedFileName = getTempDerivedSourceFeedFileName source

    if File.Exists feedFileName then
        let existingDerivedFeed =
            ProviderDerivedSourceFeed.Load feedFileName |> deserialiseToDerivedSourceFeed

        let nextDerivedFeed =
            { SourceUrl = existingDerivedFeed.SourceUrl
              Batches = Array.append existingDerivedFeed.Batches [| incomingBatch |] }

        File.WriteAllText(tempFeedFileName, serializeDerivedSourceFeed nextDerivedFeed |> RenderView.AsString.xmlNode)
        File.Move(tempFeedFileName, feedFileName)
        ()
    else
        // Note: gave misleading error mesage before Giraffe.ViewEngine was imported:
        // feedback was about type resolution of File.WriteAllText instead of the, you know, missing import
        let derivedSourceFeed = { SourceUrl = source.SourceUrl; Batches = [| incomingBatch |]  }
        File.WriteAllText(feedFileName, serializeDerivedSourceFeed derivedSourceFeed |> RenderView.AsString.xmlNode)

    // This will change once object storage is here
    ()


let getRssItemsAbsentFromDerivedFeed (source: SourcesConfiguration.Source) (incomingRssItems: MinimalRssItem array) =
    let feedFileName = getDerivedSourceFeedFileName source

    let existingDerivedFeed =
        ProviderDerivedSourceFeed.Load feedFileName |> deserialiseToDerivedSourceFeed

    let existingBatchItems =
        existingDerivedFeed.Batches |> Array.map _.BatchItems |> Array.concat

    incomingRssItems
    |> Array.filter (fun incomingItem ->
        let maybeMatch =
            existingBatchItems
            |> Array.tryFind (fun existingItem ->
                existingItem.Item.Title = incomingItem.Title
                && existingItem.Item.Guid = incomingItem.Guid
                && existingItem.Item.Link = incomingItem.Link)

        maybeMatch.IsSome)
