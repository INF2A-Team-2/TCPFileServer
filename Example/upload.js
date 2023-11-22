const fs = require("fs");
const WebSocket = require("ws");

const socket = new WebSocket("ws://127.0.0.1:11000");
socket.binaryType = "arraybuffer";

const sendData = () => {
    console.log("Sending data");

    fs.readFile("test.jpg", (err, data) => {
        let d = new TextEncoder().encode(JSON.stringify({
            id: 5,
            mimeType: "image/jpg",
            size: data.byteLength,
        }));
        console.log(d);
        socket.send(d);

        socket.send(data);
    });

};

socket.on("open", () => {
    console.log("opened");
    sendData();
});

socket.on("message", (event) => {
    console.log("message received");
    const textDecoder = new TextDecoder('utf-8');
    const msg = textDecoder.decode(event.data);
    console.log(msg);
});

socket.on("error", (error) => {
    console.error(error);
    const textDecoder = new TextDecoder('utf-8');
    const msg = textDecoder.decode(error.rawPacket);
    console.log(msg);
});