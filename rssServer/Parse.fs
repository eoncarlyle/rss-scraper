module Scrape

open System.Globalization
open FsHttp
open AngleSharp.Html.Parser
open System
open AngleSharp.Html.Dom
open Giraffe.ViewEngine

let parser = HtmlParser()


type DirectScrapePost =
    { PostTitle: String
      Link: String
      Description: String
      Date: String }

type DirectScrapeFeed =
    { FeedTitle: String
      Link: String
      Description: String }

let internal directScrapeRssItem (post: DirectScrapePost) =
    tag
        "item"
        []
        [ tag "title" [] [ encodedText post.PostTitle ]
          tag "link" [] [ encodedText post.Link ]
          tag "pubDate" [] [ encodedText post.Date ]
          tag "description" [] [ encodedText post.Description ] ]

let internal fallbackPosts name scrapePath =
    seq {
        { PostTitle = $"Parse Failure for {name}"
          Link = scrapePath
          Date = DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss UTC", CultureInfo.InvariantCulture)
          Description = "Parse Failure" }
    }

let internal rssChannelView feed (items: XmlNode seq) =
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

let internal getDoc path =
    http { GET path } |> Request.send |> Response.toText |> parser.ParseDocument

module TheDispatch =
    let scrapePath newsletterSlug =
        $"https://thedispatch.com/newsletter/{newsletterSlug}"

    let feed (newsletterSlug: String) =
        { FeedTitle = $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(newsletterSlug.ToLower())}: The Dispatch"
          Link = scrapePath newsletterSlug
          Description = $"Simple scraper for The Dispatch: {newsletterSlug}" }

    let getFormattedIsoDate (isoTimestamp: string) =
        DateTimeOffset
            .Parse(isoTimestamp, CultureInfo.InvariantCulture)
            .ToUniversalTime()
            .ToString("ddd, dd MMM yyyy HH:mm:ss UTC", CultureInfo.InvariantCulture)

    let getPosts newsletterSlug (doc: IHtmlDocument) =
        let scrapedPosts =
            doc.QuerySelectorAll "article.card-featured"
            |> Seq.choose (fun article ->
                let maybeAnchor = article.QuerySelector "h3 a" |> Option.ofObj

                let maybeDate =
                    article.QuerySelector "time"
                    |> Option.ofObj
                    |> Option.bind (fun el -> el.GetAttribute("datetime") |> Option.ofObj)
                    |> Option.map getFormattedIsoDate

                match (maybeAnchor, maybeDate) with
                | (Some anchor, Some date) ->
                    Some
                        { PostTitle = anchor.InnerHtml.Trim()
                          Link = anchor.GetAttribute "href"
                          Description = $"Scraped {DateTime.Now.ToString()}"
                          Date = date }
                | _ -> None)

        if Seq.isEmpty scrapedPosts then
            fallbackPosts newsletterSlug <| scrapePath newsletterSlug
        else
            scrapedPosts

    let getRss newsletterSlug =
        fun () ->
            scrapePath newsletterSlug
            |> getDoc
            |> getPosts newsletterSlug
            |> Seq.map directScrapeRssItem
            |> rssChannelView (feed newsletterSlug)

open TheDispatch

module TheDiff =
    let scrapePath = "https://thediff.co/archive"
    let basePath = "https://thediff.co"

    let feed =
        { FeedTitle = "The Diff"
          Link = scrapePath
          Description = "Simple scraper for The Diff" }

    let getFormattedIsoDate isoDate =
        let dt = DateTime.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture)
        dt.ToString("ddd, dd MMM yyyy HH:mm:ss UTC", CultureInfo.InvariantCulture)

    let getPosts (doc: IHtmlDocument) =
        let scrapedPosts =
            doc.QuerySelectorAll "ol.post-list article"
            |> Seq.choose (fun article ->
                let maybeAnchor = article.QuerySelector "h3 a" |> Option.ofObj
                let maybeDescription = article.QuerySelector ".post-item-content p" |> Option.ofObj

                let maybeIsoDate =
                    article.QuerySelector "time"
                    |> Option.ofObj
                    |> Option.bind (fun el -> el.GetAttribute("datetime") |> Option.ofObj)
                    |> Option.map getFormattedIsoDate

                match (maybeAnchor, maybeDescription, maybeIsoDate) with
                | (Some anchor, Some description, Some isoDate) ->
                    Some
                        { PostTitle = anchor.InnerHtml.Trim()
                          Link = Uri(Uri basePath, anchor.GetAttribute "href").ToString()
                          Date = isoDate
                          Description = description.InnerHtml.Trim() }
                | _ -> None)

        if Seq.isEmpty scrapedPosts then
            fallbackPosts "The Diff" scrapePath
        else
            scrapedPosts

    let getRss () =
        getDoc scrapePath
        |> getPosts
        |> Seq.map directScrapeRssItem
        |> rssChannelView feed

module RenderSummarised =
    let getRss (sinkFeed: DomainModel.SinkFeed) =
        let getRssItem (sinkItem: DomainModel.SinkItem) =
            tag
                "item"
                []
                [ tag "title" [] [ encodedText sinkItem.Item.Title ]
                  tag "guid" [] [ encodedText (Option.defaultValue "" sinkItem.Item.Guid) ]
                  tag "link" [] [ encodedText (Option.defaultValue "" sinkItem.Item.Link) ]
                  tag "description" [] [ encodedText sinkItem.Item.Description ]
                  tag "content" [] [ rawText sinkItem.Item.Content ]
                  tag "pubDate" [] [ encodedText sinkItem.Item.Title ] ] 
        
        tag
            "rss"
            [ attr "version" "2.0" ]
            [
                tag
                    "channel"
                    []
                    [
                        tag "title" [] [ encodedText sinkFeed.Title ]
                        tag "link" [] [ encodedText sinkFeed.Link ]
                        tag "description" [] [encodedText sinkFeed.Description]
                        yield! Array.map getRssItem sinkFeed.Items
                    ]
            ]