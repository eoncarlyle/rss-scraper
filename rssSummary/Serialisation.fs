module Serialisation

open System.Text.RegularExpressions
open AngleSharp.Common
open DomainModels
open Anthropic.Models.Messages.Batches
open Giraffe.ViewEngine

let serializeProcessingStatus (status: ProcessingStatus) =
    match status with
    | ProcessingStatus.InProgress -> "in_progress"
    | ProcessingStatus.Canceling -> "canceling"
    | ProcessingStatus.Ended -> "ended"
    | _ -> "unknown"

let serializeItem (item: MinimalRssItem) =
    tag
        "Item"
        []
        [ tag "Title" [] [ str item.Title ]
          tag "Description" [] [ str item.Description ]
          tag "Content" [] [ str item.Content ]
          yield! item.Guid |> Option.map (fun g -> tag "Guid" [] [ str g ]) |> Option.toList
          yield! item.Link |> Option.map (fun l -> tag "Link" [] [ str l ]) |> Option.toList
          yield!
              item.PubDate
              |> Option.map (fun d -> tag "pubDate" [] [ str (d.ToString("R")) ])
              |> Option.toList ]

let serializeBatchItem (batchItem: BatchRssItem) =
    tag
        "BatchItem"
        []
        [ tag "Guid" [] [ str batchItem.Guid ]
          tag "Included" [] [ str (if batchItem.Included then "true" else "false") ]
          serializeItem batchItem.Item
          yield!
              batchItem.Result
              |> Option.map (fun r -> tag "Result" [] [ str r ])
              |> Option.toList ]

let serializeBatch (batch: SourceFeedSummaryRequestBatch) =
    tag
        "Batch"
        []
        [ tag "Id" [] [ str batch.Id ]
          tag "ProcessingStatus" [] [ str (serializeProcessingStatus batch.ProcessingStatus) ]
          yield!
              batch.ResultsUrl
              |> Option.map (fun u -> tag "ResultsUrl" [] [ str u ])
              |> Option.toList
          tag "a" [] [ batch.BatchItems |> Array.map serializeBatchItem ]]

let serializeDerivedSourceFeed (feed: DerivedSourceFeed) =
    tag
        "DerivedSourceFeed"
        []
        [ tag "SourceUrl" [] [ str feed.SourceUrl ]
          yield! feed.Batches |> Array.map serializeBatch ]

let deserialiseProcessingStatus s =
    match s with
    | "in_progress" -> ProcessingStatus.InProgress
    | "canceling" -> ProcessingStatus.Canceling
    | "ended" -> ProcessingStatus.Ended
    | unknown -> failwith $"Unknown ProcessingStatus: {unknown}"


let htmlSanitized (s: string) =
    let doc = HtmlAgilityPack.HtmlDocument()
    doc.LoadHtml s
    doc.DocumentNode.InnerText
   
let firstSentenceRemove (s: string) =
    // TODO: this parsing is bad and you know it
    let secondMatch = Regex.Matches(s, "^(.+)")[0]
    let firstSentence = secondMatch.Groups[1].Value
    s.Replace(firstSentence, "")
    
let deserialiseToDerivedSourceFeed (providerFeed: ProviderDerivedSourceFeed.DerivedSourceFeed) : DerivedSourceFeed =
    { SourceUrl = providerFeed.SourceUrl
      Batches =
        providerFeed.Batches
        |> Array.map (fun batch ->
            { Id = batch.Id
              ProcessingStatus = deserialiseProcessingStatus batch.ProcessingStatus
              ResultsUrl = batch.ResultsUrl
              BatchItems =
                batch.BatchItems
                |> Array.map (fun batchItem ->
                    { Guid = batchItem.Guid
                      Included = batchItem.Included
                      Result = batchItem.Result
                      Item =
                        { Title = batchItem.Item.Title
                          Guid = batchItem.Item.Guid
                          Link = batchItem.Item.Link
                          Description = batchItem.Item.Description
                          Content = batchItem.Item.Content
                          PubDate = batchItem.Item.PubDate } }) }) }
