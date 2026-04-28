module AppGeminiCommon

open System
open System.Threading
open System.Threading.Tasks
open DomainModel
open Google.GenAI
open Google.GenAI.Types
open Tiktoken.Encodings
open LanguageModelCommon
open Tiktoken

type GeminiClient = Google.GenAI.Client

let internal isTerminalState (state: JobState) =
    state = JobState.JobStateSucceeded
    || state = JobState.JobStateFailed
    || state = JobState.JobStateCancelled
    || state = JobState.JobStateExpired
    || state = JobState.JobStatePartiallySucceeded

let internal applyBatchResult (batchResponses: Map<string, InlinedResponse>) (derivedItem: DerivedItem) =
    if not (batchResponses.ContainsKey derivedItem.Guid) then
        derivedItem
    else
        let inlinedResponse = Map.find derivedItem.Guid batchResponses

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
            { derivedItem with
                Result = Some resultText }
        else
            derivedItem

type GeminiService(client: GeminiClient) =
    let modelSubmitSemaphore = new SemaphoreSlim(1)
    let encoder = Encoder(O200KBase())
    let clientCooldown = 100

    member private _.GetRequestsWithExcludes
        (items: (RssItem * Guid) array)
        (submitBatchParameters: SummaryRequestParameters)
        =
        let requests =
            requestsWithTokenCount items encoder
            |> Array.filter (filterPredicate submitBatchParameters)
            |> Array.map (fun itemTokenRecord ->
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

        requests, Array.filter (filterPredicate submitBatchParameters >> not) (requestsWithTokenCount items encoder)

    member private _.ClientBatchRequest (model: string) (requests: InlinedRequest array) =
        task {
            modelSubmitSemaphore.WaitAsync() |> ignore

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

    member this.SubmitBatch (items: RssItem array) (batchParameters: SummaryRequestParameters) =
        this.SubmitModelAgnosticBatch (Some "gemini-2.5-flash-lite") items batchParameters

    member this.SubmitModelAgnosticBatch
        (maybeModel: string option)
        (items: RssItem array)
        (summaryRequestParameters: SummaryRequestParameters)
        =
        let model = Option.defaultValue "gemini-2.5-flash-lite" maybeModel

        task {
            let itemsWithRequestGuids = Array.map (fun item -> item, Guid.NewGuid()) items

            let requestWithExcludes =
                this.GetRequestsWithExcludes itemsWithRequestGuids summaryRequestParameters

            let! response = this.ClientBatchRequest model (fst requestWithExcludes)
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

    member _.GetUpdatedDerivedFeed
        (sourceSetting: SourceSetting)
        (derivedFeed: DerivedFeed)
        : Task<DerivedFeed option> =
        let inProgressBatches =
            derivedFeed.Batches
            |> Array.filter (fun batch -> batch.ProcessingStatus = InProgress)

        task {
            let! retrievedBatches =
                inProgressBatches
                |> Array.map (fun b -> client.Batches.GetAsync b.Id)
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
                        derivedFeed.Batches
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
                        { SourceUrl = derivedFeed.SourceUrl
                          Batches = newBatches }
        }

    member this.Actions: LanguageModelActions =
        { SubmitBatch = fun items batchParameters -> this.SubmitBatch items batchParameters
          GetUpdatedDerivedFeed = fun setting feed -> this.GetUpdatedDerivedFeed setting feed }

    member private _.SubmitInstant (model: string) (itemTuple: RssItem * Guid) : Task<DerivedItem> =
        task {
            let! response = client.Models.GenerateContentAsync(model, fst itemTuple |> getStructuredQuery)

            let description =
                try
                    response.Candidates[0].Content.Parts[0].Text
                with _ ->
                    "Language model query parse failure"

            return
                { Guid = snd itemTuple |> _.ToString()
                  Included = true
                  Item = fst itemTuple
                  Result = Some description }
        }

    member this.SubmitSynchronousBatch (items: RssItem array) (summaryRequestParameters: SummaryRequestParameters) =
        this.SubmitSynchronousModelAgnosticBatch
            (Gemini31FlashLitePreview |> Serialisation.serialise |> Some)
            items
            summaryRequestParameters

    member this.SubmitSynchronousModelAgnosticBatch
        (maybeModel: string option)
        (items: RssItem array)
        (summaryRequestParameters: SummaryRequestParameters)
        =
        let model =
            (Serialisation.serialise Gemini31FlashLitePreview, maybeModel)
            ||> Option.defaultValue

        let encoder = Encoder(O200KBase())

        task {
            let itemsWithRequestGuids = Array.map (fun item -> item, Guid.NewGuid()) items

            let! batchItems =
                itemsWithRequestGuids
                |> Array.map (fun itemTuple ->
                    let tokenCount = encoder.CountTokens(fst itemTuple |> getStructuredQuery)

                    if tokenCount < summaryRequestParameters.InputTokenCutoff then
                        this.SubmitInstant model itemTuple
                    else
                        task {
                            return
                                { Guid = snd itemTuple |> _.ToString()
                                  Included = false
                                  Item = fst itemTuple
                                  Result = None }
                        })
                |> Task.WhenAll

            return
                { Id = $"synchronous/{Guid.NewGuid()}"
                  ProcessingStatus = Ended
                  BatchItems = batchItems }
        }

    member _.GetUpdatedDerivedFeedSynchronous
        (sourceSetting: SourceSetting)
        (derivedFeed: DerivedFeed)
        : Task<DerivedFeed option> =
        task { return Some derivedFeed }

    member this.SynchronousActions: LanguageModelActions =
        { SubmitBatch = this.SubmitSynchronousBatch
          GetUpdatedDerivedFeed = this.GetUpdatedDerivedFeedSynchronous }
