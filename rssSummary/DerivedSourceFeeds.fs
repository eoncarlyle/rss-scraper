module DerivedSourceFeeds

open Giraffe.ViewEngine
open FSharp.Data
open System.IO
open Models

type SourcesConfiguration = XmlProvider<"SourcesConfiguration.xml">

let sourcesConfiguration = SourcesConfiguration.Load "SourcesConfiguration.xml"

let getDerivedSourceFeedFileName (source: SourcesConfiguration.Source) =
    // mod2 j is equals
    // mod1
    $"{source.SourceSlug}.derived.xml"

let updateDerivedSourceFeed (source: SourcesConfiguration.Source) (batch: SourceFeedSummaryRequestBatch) =
    let sourceFeedFileName = getDerivedSourceFeedFileName source

    if File.Exists sourceFeedFileName then

        ()
    else
        let xml = Serialisation.serializeBatch batch
        // Note: gave misleading error mesage before Giraffe.ViewEngine was imported:
        // feedback was about type resolution of File.WriteAllText instead of the, you know, missing import
        File.WriteAllText(sourceFeedFileName, xml |> RenderView.AsString.xmlNode)

    // This will change once object storage is here
    ()
