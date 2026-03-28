module Serialisation

open Models
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
        "item"
        []
        [ tag "title" [] [ str item.Title ]
          tag "description" [] [ str item.Description ]
          tag "content" [] [ str item.Content ]
          yield! item.Guid |> Option.map (fun g -> tag "guid" [] [ str g ]) |> Option.toList
          yield! item.Link |> Option.map (fun l -> tag "link" [] [ str l ]) |> Option.toList
          yield!
              item.PubDate
              |> Option.map (fun d -> tag "pubDate" [] [ str (d.ToString("R")) ])
              |> Option.toList ]

let serializeBatchItem (batchItem: BatchRssItem) =
    tag
        "batchItem"
        []
        [ tag "guid" [] [ str batchItem.Guid ]
          tag "included" [] [ str (if batchItem.Included then "true" else "false") ]
          serializeItem batchItem.Item
          yield!
              batchItem.Result
              |> Option.map (fun r -> tag "result" [] [ str r ])
              |> Option.toList ]

let serializeBatch (batch: SourceFeedSummaryRequestBatch) =
    tag
        "batch"
        []
        [ tag "id" [] [ str batch.ID ]
          tag "processingStatus" [] [ str (serializeProcessingStatus batch.ProcessingStatus) ]
          yield!
              batch.ResultsUrl
              |> Option.map (fun u -> tag "resultsUrl" [] [ str u ])
              |> Option.toList
          yield! batch.BatchItems |> Array.map serializeBatchItem ]

let parseFeed (xml: string) : DerivedSourceFeed =
    let doc = ProviderDerivedSourceFeed.Parse(xml)
    let channel = doc.Channel

    { Channel =
        { Link =
            channel.Link Batches = channel.Batches.Batches
            |> Array.map (fun batch ->
                { ID =
                    batch.Id ProcessingStatus
                    = parseStatus batch.ProcessingStatus ResultsUrl
                    = batch.ResultsUrl BatchItems
                    = batch.BatchItems.BatchItems
                    |> Array.map (fun bi ->
                        let item = bi.Item

                        { Guid =
                            bi.Guid Included
                            = bi.Included Result
                            = bi.Result Item
                            = { Title =
                                  item.Title Guid
                                  = item.Guid Link
                                  = item.Link Description
                                  = item.Description Content
                                  = item.Content PubDate
                                  = item.PubDate
                                  |> Option.map DateTimeOffset.Parse } }) }) } }
