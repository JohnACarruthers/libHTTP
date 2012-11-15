﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;

namespace FragLabs.HTTP
{
    /// <summary>
    /// Writes HTTP responses to a socket.
    /// </summary>
    class HttpResponseWriter
    {
        /// <summary>
        /// Socket to send response on.
        /// </summary>
        Socket socket;
        /// <summary>
        /// Http response to send.
        /// </summary>
        HttpResponse response;
        /// <summary>
        /// Http request responding to.
        /// </summary>
        HttpRequest request;
        /// <summary>
        /// Async arguments when calling Socket.SendAsync
        /// </summary>
        SocketAsyncEventArgs asyncArgs;

        /// <summary>
        /// Creates a new response writer for the given socket.
        /// </summary>
        /// <param name="sock">Socket to write the response to.</param>
        public HttpResponseWriter(Socket sock)
        {
            socket = sock;
            asyncArgs = new SocketAsyncEventArgs();
            asyncArgs.Completed += ProcessSend;
        }

        /// <summary>
        /// Send data.
        /// </summary>
        /// <param name="data"></param>
        void AsyncSend(byte[] data, int dataLen)
        {
            //  todo: use global async args?
            var asyncArgs = new SocketAsyncEventArgs();
            asyncArgs.Completed += ProcessSend;
            asyncArgs.SetBuffer(data, 0, dataLen);
            if (!socket.SendAsync(asyncArgs))
                ProcessSend(socket, asyncArgs);
        }

        /// <summary>
        /// Callback from an AsyncSend operation.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ProcessSend(object sender, SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                SendBody();
            }
            else
            {
                //  error
                Close();
            }
        }

        private bool Closed = false;
        void Close()
        {
            if (Closed)
                return;

            response.Producer.Disconnect();
            response.Producer.Dispose();
            socket.Close();
            socket.Dispose();
            Closed = true;
        }

        /// <summary>
        /// Start writing the HTTP response.
        /// </summary>
        /// <param name="response"></param>
        public void AsyncWrite(HttpRequest request, HttpResponse response)
        {
            this.request = request;
            this.response = response;
            SendHeaders();
        }

        /// <summary>
        /// Sends HTTP response headers.
        /// </summary>
        void SendHeaders()
        {
            var extraHeaders = response.Producer.AdditionalHeaders();
            if (extraHeaders != null && extraHeaders.Count > 0)
            {
                foreach (var kvp in extraHeaders)
                {
                    if (!response.Headers.ContainsKey(kvp.Key))
                    {
                        response.Headers.Add(kvp.Key, kvp.Value);
                    }
                }
            }

            var disallowedHeaders = new string[] { "Server", "Connection" };
            foreach (var header in disallowedHeaders)
            {
                if (response.Headers.ContainsKey(header))
                    response.Headers.Remove(header);
            }

            var text = response.HttpVersionString + " " + (int)response.StatusCode + " " + response.StatusCode.ToString() + "\r\n";
            text += "Server: libHTTP/1.0\r\n";
            text += "Connection: Close\r\n";
            foreach (var kvp in response.Headers)
            {
                text += kvp.Key + ": " + kvp.Value + "\r\n";
            }
            text += "\r\n";
            var data = Encoding.UTF8.GetBytes(text);
            AsyncSend(data, data.Length);
        }

        /// <summary>
        /// Sends the next part of the HTTP response body.
        /// </summary>
        void SendBody()
        {
            if (!response.Producer.Connected)
                response.Producer.Connect(request);
            var args = new ProducerEventArgs();
            args.Completed += ProducerCallback;
            if (!response.Producer.ReadAsync(args))
                ProducerCallback(response.Producer, args);
        }

        /// <summary>
        /// Response producer callback.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ProducerCallback(object sender, ProducerEventArgs e)
        {
            if (e.Buffer != null)
            {
                AsyncSend(e.Buffer, e.ByteCount);
            }
            else
            {
                Close();
            }
        }
    }
}