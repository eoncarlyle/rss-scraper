module AppAnthropic

open System.Threading.Tasks
open System.Threading
open Anthropic.Models.Beta.Messages.Batches
open FsHttp.Helper
open Giraffe.ComputationExpressions
open Giraffe.ViewEngine
open System.Collections.Generic
open Microsoft.FSharp.Collections
open Microsoft.FSharp.Core
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

        let messageBatch =
            client.Messages.Batches.Create(BatchCreateParams(Requests = requests))

        Thread.Sleep(clientCooldown)
        modelSubmitSemaphore.Release() |> ignore
        return! messageBatch
    }


let internal submitModelAgnosticBatch maybeTokenCutoff maybeModel (items: MinimalRssItem array) systemPrompt =
    let model = Option.defaultValue "claude-haiku-4-5" maybeModel
    let tokenCutoff = Option.defaultValue 50000 maybeTokenCutoff

    // TODO internal semaphroe thing
    task {
        let itemsWithRequestGuids = Array.map (fun item -> item, Guid.NewGuid()) items

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
              ProcessingStatus = DomainModels.InProgress
              BatchItems = batchItems }
    }

let submitBatch (items: MinimalRssItem array) systemPrompt =
    submitModelAgnosticBatch None None items systemPrompt

let submitSpecialBatchFactory =
    fun tokenCutoff model -> submitModelAgnosticBatch (Some tokenCutoff) (Some model)

let collectAsyncEnumerable (asyncEnum: IAsyncEnumerable<'t>) =
    task {
        let results = ResizeArray()
        let enumerator = asyncEnum.GetAsyncEnumerator()
        let mutable hasMore = true

        while hasMore do
            let! moved = enumerator.MoveNextAsync()

            if moved then
                results.Add(enumerator.Current)
            else
                hasMore <- false

        return results.ToArray()
    }

let applyBatchResult (batchResponses: Map<string, MessageBatchIndividualResponse>) (batchItem: BatchRssItem) =
    if not (batchResponses.ContainsKey batchItem.Guid) then
        batchItem
    else
        let batchResponse =
            Map.find batchItem.Guid batchResponses
            |> _.Result
            |> _.Match(
                succeeded = (fun s -> Ok(s.Message)),
                errored = (fun er -> Error(Some er)),
                canceled = (fun _ -> Error(None)),
                expired = (fun _ -> Error(None))
            )

        let parsedText =
            batchResponse
            |> Result.map (fun okResponse ->
                let block = okResponse.Content |> Seq.head
                let mutable tb = Unchecked.defaultof<TextBlock>
                block.TryPickText(&tb) |> ignore
                tb.Text)

        let resultText =
            parsedText
            |> Result.defaultWith (fun err ->
                err
                |> Option.map _.Error.Error.Message
                |> Option.defaultValue "Unknown error")

        if batchResponse.IsOk then
            { batchItem with Result = Some resultText }
        else
            batchItem

let getUpdatedDerivedFeed
    (source: SourceConfig)
    (derivedSourceFeed: DerivedSourceFeed)
    : Task<DerivedSourceFeed option> =

    let inProgressBatches =
        derivedSourceFeed.Batches
        |> Array.filter (fun batch -> batch.ProcessingStatus = InProgress)

    task {
        let! inProgressBatches =
            inProgressBatches
            |> Array.map (fun b -> client.Messages.Batches.Retrieve(BatchRetrieveParams(MessageBatchID = b.Id)))
            |> Task.WhenAll

        let! inProgressBatchResults =
            inProgressBatches
            |> Array.filter (fun b -> b.ProcessingStatus = ProcessingStatus.Ended)
            |> Array.map (fun b ->
                task {
                    let! batchResults =
                        client.Messages.Batches.ResultsStreaming(BatchResultsParams(MessageBatchID = b.ID))
                        |> collectAsyncEnumerable

                    return b.ID, batchResults
                })
            |> Task.WhenAll

        let finishedBatchIds = Map inProgressBatchResults

        return
            if finishedBatchIds.Count = 0 then
                None
            else
                // Merge
                let newBatches =
                    derivedSourceFeed.Batches
                    |> Array.map (fun batch ->
                        if finishedBatchIds.ContainsKey batch.Id then
                            let batchResponses =
                                finishedBatchIds[batch.Id]
                                |> Array.map (fun result -> result.CustomID, result)
                                |> Map

                            let newBatchItems =
                                batch.BatchItems |> Array.map (applyBatchResult batchResponses)

                            { Id = batch.Id
                              ProcessingStatus = DomainModels.ProcessingStatus.Ended
                              BatchItems = newBatchItems }
                        else
                            batch)

                Some { SourceUrl = derivedSourceFeed.SourceUrl; Batches = newBatches }
    }

let Haiku45Actions: LangaugeModelActions =
    { SubmitBatch = submitBatch
      GetUpdatedDerivedFeed = getUpdatedDerivedFeed }
