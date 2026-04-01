module LanguageModelCommon
open DomainModels
open Queries
open Tiktoken

let requestsWithTokenCount items (encoder: Encoder): ItemTokenRecord array =
    items
    |> Array.map (fun item ->
        { MinimalRssItem = fst item
          TokenCount = fst item |> getStructuredQuery |> encoder.CountTokens
          Guid = snd item })

let filterPredicate submitBatchParameters =
    fun (itemTokenGuid: ItemTokenRecord) -> itemTokenGuid.TokenCount < submitBatchParameters.InputTokenCutoff
