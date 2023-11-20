const fs= require("fs");
const net = require("net");

const socket = new net.Socket();

function connect() {
    socket.connect(11000, "127.0.0.1", () => {
        sendData();
    });
}

socket.on("data", (data) => {
    const msg = data.toString("ascii");
    console.log("Message from server ", msg);

    socket.destroy();

    if (msg === "error_json") {
        console.log("Error, retrying...");
        connect();
    }
});

socket.on("error", () => {

})

function sendData()
{
    console.log("Sending file...");
    fs.readFile("test.jpg", (err, data) => {
        const fileData = {
            id: 1,
            mimeType: "image/png",
            size: data.length
        };

        socket.write(Buffer.from(JSON.stringify(fileData)));

        socket.write(data);

        console.log("Sent file");
    });
}

connect();