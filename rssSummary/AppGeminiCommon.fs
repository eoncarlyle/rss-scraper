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

let internal client = new Client()
let internal modelSubmitSemaphore = new SemaphoreSlim(1)
let internal encoder = Encoder(O200KBase())
let internal clientCooldown = 100

let internal isTerminalState (state: JobState) =
    state = JobState.JobStateSucceeded
    || state = JobState.JobStateFailed
    || state = JobState.JobStateCancelled
    || state = JobState.JobStateExpired
    || state = JobState.JobStatePartiallySucceeded

module AppGemini =

    let internal getRequestsWithExcludes (items: (RssItem * Guid) array) submitBatchParameters =
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

    let internal submitModelAgnosticBatch maybeModel (items: RssItem array) summaryRequestParameters =
        let model = Option.defaultValue "gemini-2.5-flash-lite" maybeModel

        task {
            let itemsWithRequestGuids = Array.map (fun item -> item, Guid.NewGuid()) items

            let requestWithExcludes =
                getRequestsWithExcludes itemsWithRequestGuids summaryRequestParameters

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

    let submitBatch (items: RssItem array) summaryRequestParameters =
        submitModelAgnosticBatch (Some "gemini-2.5-flash-lite") items summaryRequestParameters

    let submitSpecialBatchFactory = fun model -> submitModelAgnosticBatch (Some model)


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

    let getUpdatedDerivedFeed (sourceSetting: SourceSetting) (derivedFeed: DerivedFeed) : Task<DerivedFeed option> =

        let inProgressBatches =
            derivedFeed.Batches
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

    let AppGemini25FlashActions: LanguageModelActions =
        { SubmitBatch = submitBatch
          GetUpdatedDerivedFeed = getUpdatedDerivedFeed }

module AppGeminiSynchronous =
    let internal submitInstant model (itemTuple: RssItem * Guid) : Task<DerivedItem> =
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


    let internal submitModelAgnosticBatch maybeModel items (summaryRequestParameters: SummaryRequestParameters) =
        let model = Option.defaultValue "gemini-2.5-flash-lite" maybeModel

        task {
            let itemsWithRequestGuids = Array.map (fun item -> item, Guid.NewGuid()) items

            let! batchItems =
                itemsWithRequestGuids
                |> Array.map (fun itemTuple ->
                    let tokenCount = encoder.CountTokens(fst itemTuple |> getStructuredQuery)

                    if tokenCount < summaryRequestParameters.InputTokenCutoff then
                        submitInstant model itemTuple
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

    let submitBatch (items: RssItem array) summaryRequestParameters =
        submitModelAgnosticBatch (Some "gemini-2.5-flash-lite") items summaryRequestParameters

    let internal getUpdatedDerivedFeed (sourceSetting: SourceSetting) (derivedFeed: DerivedFeed) : Task<DerivedFeed option> =
        //No-op because `submitBatch` has already taken care o
        //TODO model this better
        task { return Some derivedFeed }

    let AppGemini25FlashSynchronousActions: LanguageModelActions =
        { SubmitBatch = submitBatch
          GetUpdatedDerivedFeed = getUpdatedDerivedFeed }
