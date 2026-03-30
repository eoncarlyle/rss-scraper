module ObjectStorage

open Amazon.S3
open Amazon.S3.Model
open System
open System.IO
open System.Threading.Tasks
open DomainModels

let internal tigrisEndpoint =
    Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_S3")

let internal bucketName = Environment.GetEnvironmentVariable("TIGRIS_BUCKET")

let internal s3Client =
    new AmazonS3Client(AmazonS3Config(ServiceURL = tigrisEndpoint, ForcePathStyle = true))

let getObjectAsync (key: string) : Task<S3Object option> =
    task {
        try
            let request = GetObjectRequest(BucketName = bucketName, Key = key)
            let! response = s3Client.GetObjectAsync request
            use reader = new StreamReader(response.ResponseStream)
            let! content = reader.ReadToEndAsync()

            return
                Some
                    { Content = content
                      ETag = response.ETag }
        with :? AmazonS3Exception as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound ->
            return None
    }


let putObjectAsync key content (maybeETag: string option) =
    task {
        try
            let request =
                PutObjectRequest(
                    BucketName = bucketName,
                    Key = key,
                    ContentBody = content,
                    DisablePayloadSigning = true
                )

            maybeETag |> Option.iter (fun eTag -> request.IfMatch <- eTag)

            let! update = s3Client.PutObjectAsync request
            return Ok update.ETag
        with :? AmazonS3Exception as ex ->
            return Error ex.StatusCode
    }

let objectExistsAsync (key: string) =
    task {
        try
            let request = GetObjectMetadataRequest(BucketName = bucketName, Key = key)
            let! _ = s3Client.GetObjectMetadataAsync(request)
            return true
        with :? AmazonS3Exception as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound ->
            return false
    }
