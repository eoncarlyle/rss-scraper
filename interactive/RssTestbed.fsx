#r "nuget: FSharp.Data"
#r "nuget: HtmlAgilityPack"
#r "nuget: Anthropic"
#r "nuget: Tiktoken"
#r "nuget: Giraffe.ViewEngine"
#r "nuget: FsHttp"

open FSharp.Data
open Giraffe.ViewEngine
open System
open System.Threading.Tasks
open Anthropic
open Anthropic.Models.Messages.Batches
open Anthropic.Models.Messages
open Tiktoken.Encodings
open Tiktoken

let encoder = Encoder(O200KBase())

let sanitized (s: string) =
    let doc = HtmlAgilityPack.HtmlDocument()
    doc.LoadHtml s
    doc.DocumentNode.InnerText

type MinimalRssItem =
    { Title: String
      Guid: String option
      Link: String option
      Description: String
      Content: String
      PubDate: DateTimeOffset option }

type BatchRssItem =
    { Guid: String
      Included: Boolean
      Item: MinimalRssItem
      Result: String option }

type SourceFeedBatch =
    { ID: String
      ProcessingStatus: ProcessingStatus
      ResultsUrl: String option
      BatchItems: BatchRssItem array }

type SourceFeedInternal =
    { Link: String
      Batches: SourceFeedBatch array }

type SourceFeed = { Channel: SourceFeedInternal }

let getStructuredQuery item =
    $"<description>{item.Description}</description><content>{item.Content}</content>"

module Serialisation =
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

    let serializeBatch (batch: SourceFeedBatch) =
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

    let serializeFeed (feed: SourceFeed) =
        tag
            "channel"
            []
            [ tag "link" [] [ str feed.Channel.Link ]
              yield! feed.Channel.Batches |> Array.map serializeBatch ]

module Artemis =
    open FsHttp

    type ArtemisRss = XmlProvider<"artemis.rss">

    let localArtemisRss = ArtemisRss.Load "artemis.rss"

    let deserialiseRssItem (item: ArtemisRss.Item) =
        { Title = item.Title
          Guid = Some item.Guid.Value
          Link = Some item.Link
          Description = sanitized item.Description
          Content = sanitized item.Encoded
          PubDate = Some item.PubDate }

    let localArtemisItems =
        localArtemisRss.Channel.Items |> Array.map deserialiseRssItem

    let getArtemisRssItems url =
        let request =
            http {
                GET url
                CacheControl "no-cache"
                body
            }

        task {
            let! response = request |> Request.sendTAsync
            let body = Response.toText response
            let feed = body |> ArtemisRss.Parse
            return Array.map deserialiseRssItem feed.Channel.Items
        }

module Anthropic =
    let internal client = new AnthropicClient()

    type ItemTokenRecord =
        { MinimalRssItem: MinimalRssItem
          TokenCount: Int32
          Guid: Guid }

    let internal getRequestsWithExcludes (items: (MinimalRssItem * Guid) array) model systemPrompt (tokenCutoff: int) =

        let requestsWithTokenCount: ItemTokenRecord array =
            items
            |> Array.map (fun item ->
                { MinimalRssItem = fst item
                  TokenCount = fst item |> getStructuredQuery |> encoder.CountTokens
                  Guid = snd item })

        let filterPredicate =
            fun (itemTokenGuid: ItemTokenRecord) -> itemTokenGuid.TokenCount < tokenCutoff

        let requests =
            requestsWithTokenCount
            |> Array.filter filterPredicate
            |> Array.map (fun itemTokenRecord ->
                let customID = itemTokenRecord.Guid

                Request(
                    CustomID = customID.ToString(),
                    Params =
                        Params(
                            MaxTokens = 1024L,
                            Model = model,
                            System =
                                ParamsSystem(
                                    [ TextBlockParam(Text = systemPrompt, CacheControl = CacheControlEphemeral()) ]
                                ),
                            Messages =
                                [ MessageParam(
                                      Role = Role.User,
                                      Content = getStructuredQuery itemTokenRecord.MinimalRssItem
                                  ) ]
                        )
                ))

        requests, Array.filter (filterPredicate >> not) requestsWithTokenCount

    let internal clientBatchRequest (requests: Request array) =
        task { return! client.Messages.Batches.Create(BatchCreateParams(Requests = requests)) }

    let submitBatch (items: MinimalRssItem array) model systemPrompt tokenCutoff =
        task {
            let itemsWithRequestGuids = Array.map (fun a -> a, System.Guid.NewGuid()) items

            let requestWithExcludes =
                getRequestsWithExcludes itemsWithRequestGuids model systemPrompt tokenCutoff

            let! response = clientBatchRequest <| fst requestWithExcludes
            let excludes = snd requestWithExcludes |> Array.map _.MinimalRssItem

            let batchItems =
                itemsWithRequestGuids
                |> Array.map (fun itemWithGuid ->
                    let item = fst itemWithGuid
                    let guid = snd itemWithGuid |> _.ToString()

                    if Array.contains item excludes then
                        { Guid = guid
                          Included = false
                          Item = item
                          Result = None }
                    else
                        { Guid = guid
                          Included = true
                          Item = item
                          Result = None })

            return
                { ID = response.ID
                  ProcessingStatus = ProcessingStatus.InProgress
                  ResultsUrl = None
                  BatchItems = batchItems }
        }

let systemPrompt =
    """
        You are summarising material for someone not working in the field who wants to stay up to speeed.
        Use only ASCII characters and format in plain text - not markdown because this is going right into
        a content tag in an RSS feed. Draw inspiration from Matt Yglesias, Bryne Hobart, Ben Thompson,
        and Patrick McKenzie in your explainations.
    """
        .Replace("\n", " ")

task {
    let! rssItems = Artemis.getArtemisRssItems "https://www.artemis.bm/feed/"
    let! sourceFeedBatch = Anthropic.submitBatch rssItems "claude-haiku-4-5" systemPrompt 50000

    let xml = Serialisation.serializeBatch sourceFeedBatch

    System.IO.File.WriteAllText("/tmp/myFile.xml", xml |> RenderView.AsString.xmlNode)
    Console.WriteLine rssItems
}
|> Task.WaitAll
