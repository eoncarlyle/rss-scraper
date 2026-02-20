module Scrape

open FsHttp
open AngleSharp.Html.Parser
open System
open AngleSharp.Html.Dom
open Giraffe.ViewEngine

let parser = HtmlParser()

type Post = { Title: String; Path: String }

let rssItem (scrapePath: string) (post: Post) = 
    tag
        "item"
        []
        [ tag "title" [] [ encodedText post.Title ]
          tag "link" [] [ encodedText $"{scrapePath}{post.Path}" ]
          tag "description" [] [ encodedText $"Scraped {DateTime.UtcNow} UTC"
        ]
    ]

let rssChannelView title link description (items: XmlNode list) = 
    tag
        "rss"
        [ attr "version" "2.0" ]
        [ tag "channel" 
            []
            [   tag "title" [] [ encodedText title ]
                tag "link" [] [ encodedText link ]
                tag "description" [] [ encodedText description ]
                yield! items
        ]]

let getDoc path =
    http { GET path } |> Request.send |> Response.toText |> parser.ParseDocument

let getLinks (doc: IHtmlDocument) =
    doc.QuerySelectorAll(".post-list h3")
    |> Seq.map _.Children
    |> Seq.concat
    |> Seq.map (function
        | :? IHtmlAnchorElement as a ->
            Some
                { Title = a.InnerHtml
                  Path = a.PathName }
        | _ -> None)
    |> Seq.choose id

let scrape path = getDoc path |> getLinks
