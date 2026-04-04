module DerivedFeeds

open System.IO
open System
open System.Threading.Tasks
open Serialisation
open DomainModel
open LanguageModelCommon


let getDerivedFeedKeyFromSlug (sourceSlug: SourceSlug) = $"{sourceSlug}.derived.json"

let getDerivedFeedKeyFromSource (sourceSetting: SourceSetting) =
    getDerivedFeedKeyFromSlug sourceSetting.SourceSlug

let crossItemEquivalent (item: RssItem) (batchItem: DerivedItem) =
    item.Title = batchItem.Item.Title
    && item.Guid = batchItem.Item.Guid
    && item.Link = batchItem.Item.Link

// Need to accept a factory to get the full item for a fresh etag: not the first time I've made this mistake

let getFreshSourceItems (sourceSetting: SourceSetting) (incomingRssItems: RssItem array) =
    task {
        let feedKey = getDerivedFeedKeyFromSource sourceSetting
        let! maybeDerivedS3Object = ObjectStorage.getS3Object feedKey

        match maybeDerivedS3Object with
        | Some derivedS3Object ->
            let derivedFeed = deserialise<DerivedFeed> derivedS3Object.Content

            let submittedBatchItems =
                derivedFeed.Batches |> Array.map _.BatchItems |> Array.concat

            if Array.length submittedBatchItems = 0 then
                return Array.truncate sourceSetting.MaximumLookback incomingRssItems
            else
                let truncationIndex =
                    incomingRssItems
                    |> Array.tryFindIndexBack (fun incomingItem ->
                        submittedBatchItems
                        |> Array.tryFind (fun batchItem -> crossItemEquivalent incomingItem batchItem)
                        |> Option.isSome)
                    |> Option.defaultValue sourceSetting.MaximumLookback

                return
                    incomingRssItems
                    |> Array.truncate truncationIndex
                    |> Array.filter (fun sourceItem ->
                        let maybeMatch =
                            submittedBatchItems
                            |> Array.tryFind (fun batchItem -> crossItemEquivalent sourceItem batchItem)

                        maybeMatch.IsNone)
        | None -> return Array.truncate sourceSetting.MaximumLookback incomingRssItems
    }

let submitSummaryBatch
    (source: SourceSetting)
    (fetchSource: string -> Task<RssItem array>)
    (modelActions: LanguageModelActions)
    =
    task {
        let! sourceItems = fetchSource source.SourceUrl
        let! submittedSourceItems = getFreshSourceItems source sourceItems

        if Array.length submittedSourceItems > 0 then
            let submitBatchParameters =
                { SystemPrompt = Option.defaultValue defaultSystemPrompt source.SystemPrompt
                  InputTokenCutoff = source.InputTokenCutoff
                  OutputTokenCutoff = source.OutputTokenCutoff }

            let! summaryRequest = modelActions.SubmitBatch submittedSourceItems submitBatchParameters

            return Some summaryRequest
        else
            return None
    }

let appendToFeed (sourceSetting: SourceSetting) (derivedBatch: DerivedBatch) =
    let append sourceSetting derivedBatch =
        task {
            let feedKey = getDerivedFeedKeyFromSource sourceSetting
            let! maybeDerivedS3Object = ObjectStorage.getS3Object feedKey

            match maybeDerivedS3Object with
            | Some derivedS3Object ->
                let existingDerivedFeed = deserialise<DerivedFeed> derivedS3Object.Content

                let updatedDerivedFeed =
                    { SourceUrl = existingDerivedFeed.SourceUrl
                      Batches = Array.append existingDerivedFeed.Batches [| derivedBatch |] }

                return! ObjectStorage.putS3Object feedKey (serialise updatedDerivedFeed) (Some derivedS3Object.ETag)
            | None ->
                let derivedFeed =
                    { SourceUrl = sourceSetting.SourceUrl
                      Batches = [| derivedBatch |] }

                return! ObjectStorage.putS3Object feedKey (serialise derivedFeed) None
        }

    ObjectStorage.retryHttp 3 (fun () -> append sourceSetting derivedBatch)

let feedUpdateWithSummaryRequests source fetchSource modelActions =
    task {
        let! maybeSubmitBatch = submitSummaryBatch source fetchSource modelActions
        match maybeSubmitBatch with
        | Some batch ->
            let! b = appendToFeed source batch
            return Some b
        | None -> return None
    }

let tryFeedUpdateWithSummaryResults sourceSetting modelActions =
    let update sourceSetting modelActions =
        task {
            let feedKey = getDerivedFeedKeyFromSource sourceSetting
            let! maybeDerivedS3Object = ObjectStorage.getS3Object feedKey

            match maybeDerivedS3Object with
            | Some s3Object ->
                let existingDerivedFeed = deserialise<DerivedFeed> s3Object.Content
                let! maybeUpdatedDerivedFeed = modelActions.GetUpdatedDerivedFeed sourceSetting existingDerivedFeed

                match maybeUpdatedDerivedFeed with
                | Some updated ->
                    let! putResult = ObjectStorage.putS3Object feedKey (serialise updated) (Some s3Object.ETag)

                    let getUpdatedCount batches =
                        batches
                        |> Array.map _.BatchItems
                        |> Array.concat
                        |> Array.filter _.Result.IsSome
                        |> Array.length

                    return
                        putResult
                        |> Result.map (fun _ ->
                            getUpdatedCount updated.Batches - getUpdatedCount existingDerivedFeed.Batches)

                | None -> return Ok 0
            | None -> return Error Net.HttpStatusCode.NotFound
        }

    ObjectStorage.retryHttp 3 (fun () -> update sourceSetting modelActions)
