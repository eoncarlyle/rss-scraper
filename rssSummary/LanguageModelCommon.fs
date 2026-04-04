module LanguageModelCommon
open DomainModel
open Tiktoken

let getStructuredQuery (item: DomainModel.RssItem) =
    $"<description>{item.Description}</description><content>{item.Content}</content>"

let defaultSystemPrompt =
    """
        You are summarising material for someone not working in the field who wants to stay up to speed with the
        content. Format in plain text, do not format in Markdown. This is going to be wrapped in a `<p>` tag,
        so don't try to use newlines for formatting. Draw inspiration from Matt Yglesias, Bryne Hobart, Ben Thompson,
        and Patrick McKenzie in your explanations. This summary is just for personal use.
    """
    |> _.Split('\n')
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> String.concat " "

let requestsWithTokenCount items (encoder: Encoder): ItemTokenRecord array =
    items
    |> Array.map (fun item ->
        { MinimalRssItem = fst item
          TokenCount = fst item |> getStructuredQuery |> encoder.CountTokens
          Guid = snd item })

let filterPredicate submitBatchParameters =
    fun (itemTokenGuid: ItemTokenRecord) -> itemTokenGuid.TokenCount < submitBatchParameters.InputTokenCutoff
