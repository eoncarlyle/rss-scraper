module AppAnthropic

open System.Threading.Tasks
open System.Threading
open Tiktoken.Encodings
open Tiktoken
open DomainModels
open Anthropic
open Anthropic.Models.Messages
open Anthropic.Models.Messages.Batches
open System
open Queries

let internal client = new AnthropicClient()
let internal modelSubmitSemaphore = new SemaphoreSlim(1)
let internal encoder = Encoder(O200KBase())
let internal clientCooldown = 100

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
    task {
        modelSubmitSemaphore.Wait()
        let messageBatch = client.Messages.Batches.Create(BatchCreateParams(Requests = requests))
        Thread.Sleep(clientCooldown)
        modelSubmitSemaphore.Release() |> ignore
        return! messageBatch
    }


let internal submitBatch maybeTokenCutoff maybeModel (items: MinimalRssItem array) systemPrompt =
    let model = Option.defaultValue "claude-haiku-4-5" maybeModel
    let tokenCutoff = Option.defaultValue 50000 maybeTokenCutoff

    // TODO internal semaphroe thing
    task {
        let itemsWithRequestGuids =
            Array.map (fun item -> item, Guid.NewGuid()) items

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
            { Id = response.ID
              ProcessingStatus = ProcessingStatus.InProgress
              ResultsUrl = None
              BatchItems = batchItems }
    }

let submitStandardBatch (items: MinimalRssItem array) systemPrompt =
    submitBatch None None items systemPrompt

let submitSpecialBatchFactory =
    fun tokenCutoff model -> submitBatch (Some tokenCutoff) (Some model)

let getBatch messageBatchId =
    client.Messages.Batches.Retrieve(BatchRetrieveParams(MessageBatchID = messageBatchId))