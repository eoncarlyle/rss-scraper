module AppGemini

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open DomainModels
open Queries
open Google.GenAI
open Google.GenAI.Types
open Tiktoken.Encodings
open Tiktoken

let internal client = new Client()
let internal modelSubmitSemaphore = new SemaphoreSlim(1)
let internal encoder = Encoder(O200KBase())
let internal clientCooldown = 100

type ItemTokenRecord =
    { MinimalRssItem: MinimalRssItem
      TokenCount: Int32
      Guid: Guid }

let internal getRequestsWithExcludes
    (items: (MinimalRssItem * Guid) array)
    submitBatchParameters
    =

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

            InlinedRequest(
                Contents =
                    ResizeArray<Content>(
                        [ Content(
                              Role = "user",
                              Parts =
                                  ResizeArray<Part>(
                                      [ Part(Text = getStructuredQuery itemTokenRecord.MinimalRssItem) ]
                                  )
                          ) ]
                    )
            ))

    requests, Array.filter (filterPredicate >> not) requestsWithTokenCount

let internal clientBatchRequest model (requests: InlinedRequest array) =
    task {
        modelSubmitSemaphore.Wait()

        try
            let src = BatchJobSource(InlinedRequests = ResizeArray<InlinedRequest>(requests))

            let timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            let config = CreateBatchJobConfig(DisplayName = $"rss-batch-{timestamp}")

            let! batchJob = client.Batches.CreateAsync(model, src, config)

            do! Task.Delay(clientCooldown)
            return batchJob
        finally
            modelSubmitSemaphore.Release() |> ignore
    }

let internal submitModelAgnosticBatch
    maybeModel
    (items: MinimalRssItem array)
    (submitBatchParameters: SubmitBatchParameters)
    =
    let model = Option.defaultValue "gemini-2.5-flash-lite" maybeModel

    task {
        let itemsWithRequestGuids = Array.map (fun item -> item, Guid.NewGuid()) items

        let requestWithExcludes =
            getRequestsWithExcludes itemsWithRequestGuids submitBatchParameters

        let! response = clientBatchRequest model <| fst requestWithExcludes
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
            { Id = response.Name
              ProcessingStatus = InProgress
              BatchItems = batchItems }
    }

let submitBatch (items: MinimalRssItem array) (submitBatchParameters: SubmitBatchParameters) =
    submitModelAgnosticBatch (Some "gemini-2.5-flash-lite") items submitBatchParameters

let submitSpecialBatchFactory = fun model -> submitModelAgnosticBatch (Some model)

let internal isTerminalState (state: JobState) =
    state = JobState.JobStateSucceeded
    || state = JobState.JobStateFailed
    || state = JobState.JobStateCancelled
    || state = JobState.JobStateExpired
    || state = JobState.JobStatePartiallySucceeded

let internal applyBatchResult (batchResponses: Map<string, InlinedResponse>) (batchItem: BatchRssItem) =
    if not (batchResponses.ContainsKey batchItem.Guid) then
        batchItem
    else
        let inlinedResponse = Map.find batchItem.Guid batchResponses

        let parsedText =
            if inlinedResponse.Error <> null then
                Error(Some inlinedResponse.Error.Message)
            elif
                inlinedResponse.Response <> null
                && not (String.IsNullOrEmpty inlinedResponse.Response.Text)
            then
                Ok inlinedResponse.Response.Text
            else
                Error None

        let resultText =
            parsedText
            |> Result.defaultWith (fun err -> err |> Option.defaultValue "Unknown error")

        if Result.isOk parsedText then
            { batchItem with
                Result = Some resultText }
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
        let! retrievedBatches =
            inProgressBatches
            |> Array.map (fun b -> client.Batches.GetAsync(b.Id))
            |> Task.WhenAll

        let finishedBatches =
            retrievedBatches
            |> Array.filter (fun b -> b.State.HasValue && isTerminalState b.State.Value)
            |> Array.map (fun b ->
                let responses =
                    if b.Dest <> null && b.Dest.InlinedResponses <> null then
                        b.Dest.InlinedResponses
                        |> Seq.choose (fun r ->
                            if r.Metadata <> null && r.Metadata.ContainsKey("key") then
                                Some(r.Metadata["key"], r)
                            else
                                None)
                        |> Map.ofSeq
                    else
                        Map.empty

                b.Name, responses)
            |> Map.ofArray

        return
            if finishedBatches.Count = 0 then
                None
            else
                let newBatches =
                    derivedSourceFeed.Batches
                    |> Array.map (fun batch ->
                        if finishedBatches.ContainsKey batch.Id then
                            let batchResponses = finishedBatches[batch.Id]

                            let newBatchItems = batch.BatchItems |> Array.map (applyBatchResult batchResponses)

                            { Id = batch.Id
                              ProcessingStatus = ProcessingStatus.Ended
                              BatchItems = newBatchItems }
                        else
                            batch)

                Some
                    { SourceUrl = derivedSourceFeed.SourceUrl
                      Batches = newBatches }
    }

let AppGeminiActions: LangaugeModelActions =
    { SubmitBatch = submitBatch
      GetUpdatedDerivedFeed = getUpdatedDerivedFeed }
