module LanguageModelCommon

open DomainModel
open Tiktoken

let getStructuredQuery (item: DomainModel.RssItem) =
    $"<description>{item.Description}</description><content>{item.Content}</content>"

let defaultSystemPrompt =
    """
        You are summarizing material for an intelligent but non-domain expert audience: the audience is a software
        engineer working in a related industry. DO NOT USE MARKDOWN FORMATTING: it will NOT work where
        this is being read. Use transition words and phrases to connect different topics smoothly. Emulate the
        analytical and insightful style of writers like Ben Thompson or Patrick McKenzie. Breaking into paragraphs is
        totally fine. For each point, don't just state the fact—briefly explain its significance or why it matters to an outsider.
        Disregard any contact information or other subscription boilerplate: focus on the signal
    """
    |> _.Split('\n')
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> String.concat " "

let requestsWithTokenCount items (encoder: Encoder) : ItemTokenRecord array =
    items
    |> Array.map (fun item ->
        { MinimalRssItem = fst item
          TokenCount = fst item |> getStructuredQuery |> encoder.CountTokens
          Guid = snd item })

let filterPredicate submitBatchParameters =
    fun (itemTokenGuid: ItemTokenRecord) -> itemTokenGuid.TokenCount < submitBatchParameters.InputTokenCutoff
