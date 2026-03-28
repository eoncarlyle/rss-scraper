module XmlParsing

let getStructuredQuery (item: Models.MinimalRssItem) =
    $"<description>{item.Description}</description><content>{item.Content}</content>"

let sanitized (s: string) =
    let doc = HtmlAgilityPack.HtmlDocument()
    doc.LoadHtml s
    doc.DocumentNode.InnerText
