module Queries

let getStructuredQuery (item: DomainModels.RssItem) =
    $"<description>{item.Description}</description><content>{item.Content}</content>"

let defaultSystemPrompt =
    """
        You are summarising material for someone not working in the field who wants to stay up to speed with the
        content. Format in plain text, do not format in Markdown. Draw inspiration from Matt
        Yglesias, Bryne Hobart, Ben Thompson, and Patrick McKenzie in your explanations. This summary is just for
        personal use.
    """
    |> _.Split('\n')
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> String.concat " "
