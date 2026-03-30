module ObjectStorage

open Amazon.S3
open Amazon.S3.Model
open System
open System.IO

let internal tigrisEndpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_S3")

let internal bucketName = Environment.GetEnvironmentVariable("TIGRIS_BUCKET")

let internal s3Client = new AmazonS3Client(AmazonS3Config(ServiceURL = tigrisEndpoint, ForcePathStyle = true))

let getObjectAsync (key: string) =
    task {
        try
            let request = GetObjectRequest(BucketName = bucketName, Key = key)
            let! response = s3Client.GetObjectAsync(request)
            use reader = new StreamReader(response.ResponseStream)
            let! content = reader.ReadToEndAsync()
            return Some content
        with :? AmazonS3Exception as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound ->
            return None
    }


let putObjectAsync (key: string) (content: string) =
    task {
        let request =
            PutObjectRequest(BucketName = bucketName, Key = key, ContentBody = content, DisablePayloadSigning = true)

        let! _ = s3Client.PutObjectAsync(request)
        return ()
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