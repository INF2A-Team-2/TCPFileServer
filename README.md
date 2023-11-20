# TCPFileServer
File server for storing large image / video files.  
Used by the VisconSupportAPI for storing attachments.

Any text data is and must be binary encoded using ASCII encoding.

## Usage
You can use any TCP client / language of your choice.

There is no handshake involved just send your after connecting and you'll get a response.

### Uploading
The upload transaction consists of 2 parts:
 - The metadata in JSON format
 - The binary data

#### JSON metadata example
```jsonc
{
    "id": 1, // File ID
    "mimeType": "image/png", // File type
    "size": 124000 // File size in bytes
}
```
#### Supported file types
 - Images
   - .png `image/png`
   - .jpeg `image/jpeg`
   - .jpg `image/jpg`
 - Videos
   - .mp4 `video/mp4`

