module Scrape

open FsHttp
open AngleSharp.Html.Parser
open System
open AngleSharp.Html.Dom
open Giraffe.ViewEngine

let parser = HtmlParser()

type Post =
    { PostTitle: String
      Link: String
      Description: String option }

type Feed =
    { FeedTitle: String
      Link: String
      Description: String }

let rssItem (post: Post) =
    tag
        "item"
        []
        [ tag "title" [] [ encodedText post.PostTitle ]
          tag "link" [] [ encodedText post.Link ]
          tag "description" [] [ encodedText (Option.defaultValue $"Scraped {DateTime.UtcNow} UTC" post.Description) ] ]

let fallbackPosts name scrapePath =
    seq {
        { PostTitle = $"Parse Failure for {name}"
          Link = scrapePath
          Description = None }
    }

let rssChannelView feed (items: XmlNode seq) =
    tag
        "rss"
        [ attr "version" "2.0" ]
        [ tag
              "channel"
              []
              [ tag "title" [] [ encodedText feed.FeedTitle ]
                tag "link" [] [ encodedText feed.Link ]
                tag "description" [] [ encodedText feed.Description ]
                yield! items ] ]

let getDoc path =
    http { GET path } |> Request.send |> Response.toText |> parser.ParseDocument

module TheDispatch =
    let scrapePath newsletterSlug =
        $"https://thedispatch.com/newsletter/{newsletterSlug}"

    let feed newsletterSlug =
        { FeedTitle = $"The Dispatch: {newsletterSlug}"
          Link = scrapePath newsletterSlug
          Description = $"Simple scraper for The Dispatch: {newsletterSlug}" }

    let getPosts newsletterSlug (doc: IHtmlDocument) =
        let scrapedPosts =
            doc.QuerySelectorAll "article.card-featured"
            |> Seq.choose (fun article ->
                let anchor = article.QuerySelector "h3 a" |> Option.ofObj
                let date = article.QuerySelector "time" |> Option.ofObj

                match anchor with
                | None -> None
                | Some a ->
                    Some
                        { PostTitle = a.InnerHtml.Trim()
                          Link = a.GetAttribute "href"
                          Description = date |> Option.map _.InnerHtml.Trim() })

        if Seq.isEmpty scrapedPosts then
            fallbackPosts newsletterSlug <| scrapePath newsletterSlug
        else
            scrapedPosts

    let getRss newsletterSlug =
        fun () ->
            scrapePath newsletterSlug
            |> getDoc
            |> getPosts newsletterSlug
            |> Seq.map rssItem
            |> Seq.rev
            |> rssChannelView (feed newsletterSlug)

module TheDiff =
    let scrapePath = "https://thediff.co/archive"
    let basePath = "https://thediff.co"

    let feed =
        { FeedTitle = "The Diff"
          Link = scrapePath
          Description = "Simple scraper for The Diff" }

    let getPosts (doc: IHtmlDocument) =
        let scrapedPosts =
            doc.QuerySelectorAll "ol.post-list article"
            |> Seq.choose (fun article ->
                let anchor = article.QuerySelector "h3 a" |> Option.ofObj
                let description = article.QuerySelector ".post-item-content p" |> Option.ofObj

                match anchor with
                | None -> None
                | Some a ->
                    Some
                        { PostTitle = a.InnerHtml.Trim()
                          Link = Uri(Uri basePath, a.GetAttribute "href").ToString()
                          Description = description |> Option.map _.InnerHtml.Trim() })

        if Seq.isEmpty scrapedPosts then
            fallbackPosts "The Diff" scrapePath
        else
            scrapedPosts

    let getRss () =
        getDoc scrapePath |> getPosts |> Seq.map rssItem |> Seq.rev |> rssChannelView feed
