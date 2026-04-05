module AppAnthropic

open System.Threading.Tasks
open System.Threading
open System.Collections.Generic
open Microsoft.FSharp.Collections
open Microsoft.FSharp.Core
open Tiktoken.Encodings
open Tiktoken
open DomainModel
open LanguageModelCommon
open Anthropic
open Anthropic.Models.Messages
open Anthropic.Models.Messages.Batches
open System

let internal collectAsyncEnumerable (asyncEnum: IAsyncEnumerable<'t>) =
    task {
        let results = ResizeArray()
        use enumerator = asyncEnum.GetAsyncEnumerator()
        let mutable hasMore = true

        while hasMore do
            let! moved = enumerator.MoveNextAsync()

            if moved then
                results.Add(enumerator.Current)
            else
                hasMore <- false

        return results.ToArray()
    }

let internal applyBatchResult (batchResponses: Map<string, MessageBatchIndividualResponse>) (derivedItem: DerivedItem) =
    if not (batchResponses.ContainsKey derivedItem.Guid) then
        derivedItem
    else
        let batchResponse =
            Map.find derivedItem.Guid batchResponses
            |> _.Result
            |> _.Match(
                succeeded = (fun s -> Ok(s.Message)),
                errored = (fun er -> Error(Some er)),
                canceled = (fun _ -> Error(None)),
                expired = (fun _ -> Error(None))
            )

        let parsedText =
            batchResponse
            |> Result.bind (fun okResponse ->
                match okResponse.Content |> Seq.tryHead with
                | Some block ->
                    let mutable tb = Unchecked.defaultof<TextBlock>
                    block.TryPickText(&tb) |> ignore
                    Ok tb.Text
                | None -> Error None)

        let resultText =
            parsedText
            |> Result.defaultWith (fun err ->
                err |> Option.map _.Error.Error.Message |> Option.defaultValue "Unknown error")

        if batchResponse.IsOk then
            { derivedItem with
                Result = Some resultText }
        else
            derivedItem

type AnthropicService(client: AnthropicClient) =
    let modelSubmitSemaphore = new SemaphoreSlim(1)
    let encoder = Encoder(O200KBase())
    let clientCooldown = 100

    member private _.GetRequestsWithExcludes(items: (RssItem * Guid) array, model: string, submitBatchParameters: SummaryRequestParameters) =
        let requests =
            requestsWithTokenCount items encoder
            |> Array.filter (filterPredicate submitBatchParameters)
            |> Array.map (fun itemTokenRecord ->
                let customID = itemTokenRecord.Guid

                Request(
                    CustomID = customID.ToString(),
                    Params =
                        Params(
                            MaxTokens = submitBatchParameters.OutputTokenCutoff,
                            Model = model,
                            System =
                                ParamsSystem(
                                    [ TextBlockParam(
                                          Text = submitBatchParameters.SystemPrompt,
                                          CacheControl = CacheControlEphemeral()
                                      ) ]
                                ),
                            Messages =
                                [ MessageParam(
                                      Role = Role.User,
                                      Content = getStructuredQuery itemTokenRecord.MinimalRssItem
                                  ) ]
                        )
                ))

        requests, Array.filter (filterPredicate submitBatchParameters >> not) (requestsWithTokenCount items encoder)

    member private _.ClientBatchRequest(requests: Request array) =
        task {
            modelSubmitSemaphore.WaitAsync() |> ignore

            try
                let messageBatch =
                    client.Messages.Batches.Create(BatchCreateParams(Requests = requests))

                do! Task.Delay(clientCooldown)
                return! messageBatch
            finally
                modelSubmitSemaphore.Release() |> ignore
        }

    member this.SubmitBatch(items: RssItem array, batchParameters: SummaryRequestParameters) =
        this.SubmitModelAgnosticBatch(Some "claude-haiku-4-5", items, batchParameters)

    member this.SubmitModelAgnosticBatch(maybeModel: string option, items: RssItem array, submitBatchParameters: SummaryRequestParameters) =
        let model = Option.defaultValue "claude-haiku-4-5" maybeModel

        task {
            let itemsWithRequestGuids = Array.map (fun item -> item, Guid.NewGuid()) items

            let requestWithExcludes =
                this.GetRequestsWithExcludes(itemsWithRequestGuids, model, submitBatchParameters)

            let! response = this.ClientBatchRequest(fst requestWithExcludes)
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
                  ProcessingStatus = InProgress
                  BatchItems = batchItems }
        }

    member _.GetUpdatedDerivedFeed(sourceSetting: SourceSetting, derivedFeed: DerivedFeed) : Task<DerivedFeed option> =
        let inProgressBatches =
            derivedFeed.Batches
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
                    let newBatches =
                        derivedFeed.Batches
                        |> Array.map (fun batch ->
                            if finishedBatchIds.ContainsKey batch.Id then
                                let batchResponses =
                                    finishedBatchIds[batch.Id]
                                    |> Array.map (fun result -> result.CustomID, result)
                                    |> Map

                                let newBatchItems = batch.BatchItems |> Array.map (applyBatchResult batchResponses)

                                { Id = batch.Id
                                  ProcessingStatus = DomainModel.ProcessingStatus.Ended
                                  BatchItems = newBatchItems }
                            else
                                batch)

                    Some
                        { SourceUrl = derivedFeed.SourceUrl
                          Batches = newBatches }
        }

    member this.Actions: LanguageModelActions =
        { SubmitBatch = fun items batchParameters -> this.SubmitBatch(items, batchParameters)
          GetUpdatedDerivedFeed = fun setting feed -> this.GetUpdatedDerivedFeed(setting, feed) }
