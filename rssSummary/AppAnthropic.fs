module AppAnthropic

open System.Threading.Tasks
open System.Threading
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
    { MinimalRssItem: RssItem
      TokenCount: Int32
      Guid: Guid }

let internal getRequestsWithExcludes (items: (RssItem * Guid) array) model submitBatchParameters =

    let requestsWithTokenCount: ItemTokenRecord array =
        items
        |> Array.map (fun item ->
            { MinimalRssItem = fst item
              TokenCount = fst item |> getStructuredQuery |> encoder.CountTokens
              Guid = snd item })

    let filterPredicate =
        fun (itemTokenGuid: ItemTokenRecord) -> itemTokenGuid.TokenCount < submitBatchParameters.InputTokenCutoff

    let requests =
        requestsWithTokenCount
        |> Array.filter filterPredicate
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

    requests, Array.filter (filterPredicate >> not) requestsWithTokenCount

let internal clientBatchRequest (requests: Request array) =
    task {
        modelSubmitSemaphore.Wait()

        try
            let messageBatch =
                client.Messages.Batches.Create(BatchCreateParams(Requests = requests))

            do! Task.Delay(clientCooldown)
            return! messageBatch
        finally
            modelSubmitSemaphore.Release() |> ignore
    }


let internal submitModelAgnosticBatch maybeModel (items: RssItem array) submitBatchParameters =
    let model = Option.defaultValue "claude-haiku-4-5" maybeModel

    task {
        let itemsWithRequestGuids = Array.map (fun item -> item, Guid.NewGuid()) items

        let requestWithExcludes =
            getRequestsWithExcludes itemsWithRequestGuids model submitBatchParameters

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
              ProcessingStatus = InProgress
              BatchItems = batchItems }
    }

let submitBatch (items: RssItem array) (summaryRequestParameters: SummaryRequestParameters) =
    submitModelAgnosticBatch (Some "claude-haiku-4-5") items summaryRequestParameters

let submitSpecialBatchFactory = fun model -> submitModelAgnosticBatch (Some model)

let collectAsyncEnumerable (asyncEnum: IAsyncEnumerable<'t>) =
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

let applyBatchResult (batchResponses: Map<string, MessageBatchIndividualResponse>) (derivedItem: DerivedItem) =
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

let getUpdatedDerivedFeed
    sourceSetting 
    (derivedFeed: DerivedFeed)
    : Task<DerivedFeed option> =

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
                // Merge
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
                              ProcessingStatus = DomainModels.ProcessingStatus.Ended
                              BatchItems = newBatchItems }
                        else
                            batch)

                Some
                    { SourceUrl = derivedFeed.SourceUrl
                      Batches = newBatches }
    }

let Haiku45Actions: LangaugeModelActions =
    { SubmitBatch = submitBatch
      GetUpdatedDerivedFeed = getUpdatedDerivedFeed }
