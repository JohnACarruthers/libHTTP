using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;

namespace FragLabs.HTTP
{
    /// <summary>
    /// Reads HTTP requests from a given socket.
    /// </summary>
    class HttpRequestReader : IDisposable
    {
        /// <summary>
        /// Event handler for when reading a request is complete.
        /// </summary>
        /// <param name="request"></param>
        public delegate void ReadCompleteEventHandler(HttpRequest request);

        /// <summary>
        /// Event triggered when reading a request is completed.
        /// </summary>
        public event ReadCompleteEventHandler ReadComplete;

        /// <summary>
        /// Event handler for erronous http requests.
        /// </summary>
        /// <param name="httpStatusCode"></param>
        public delegate void HttpErrorEventHandler(HttpStatusCode httpStatusCode, HttpRequest request);

        /// <summary>
        /// Event triggered when an http error is encountered.
        /// </summary>
        public event HttpErrorEventHandler HttpError;

        /// <summary>
        /// Socket the request is being read from.
        /// </summary>
        Socket socket = null;

        /// <summary>
        /// Arguments used for async operations.
        /// </summary>
        SocketAsyncEventArgs asyncArgs = null;

        /// <summary>
        /// Number of bytes to read, maximum, per read operation.
        /// </summary>
        int bufferSize = 4096;

        /// <summary>
        /// Request text to be processed. Does NOT contain the full body.
        /// </summary>
        string requestText = "";

        /// <summary>
        /// HTTP request being read.
        /// </summary>
        HttpRequest request = null;

        /// <summary>
        /// Is processing complete?
        /// </summary>
        bool processingComplete = false;

        /// <summary>
        /// Current request processing state.
        /// </summary>
        ProcessingState processingState = ProcessingState.InitialLine;

        /// <summary>
        /// Create a new request reader to read from the given socket.
        /// </summary>
        /// <param name="sock">Socket to read request from.</param>
        public HttpRequestReader(Socket sock)
        {
            socket = sock;
            asyncArgs = new SocketAsyncEventArgs();
            asyncArgs.SetBuffer(new byte[bufferSize], 0, bufferSize);
            asyncArgs.Completed += ProcessRecv;
            request = new HttpRequest()
            {
                ClientSocket = sock,
                Body = "",
                Headers = new Dictionary<string,string>(),
                Method = default(HttpMethod),
                Uri = null,
                Version = default(Version)
            };
        }

        /// <summary>
        /// Read the HTTP request from the socket.
        /// </summary>
        /// <returns></returns>
        public void AsyncReadRequest()
        {
            SockRecv(socket);
        }

        /// <summary>
        /// Starts an async recv operation.
        /// </summary>
        /// <param name="sock"></param>
        void SockRecv(Socket sock)
        {
            asyncArgs.SetBuffer(0, bufferSize);
            if (!sock.ReceiveAsync(asyncArgs))
                ProcessRecv(sock, asyncArgs);
        }

        /// <summary>
        /// Callback when receiving data.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        void ProcessRecv(object sender, SocketAsyncEventArgs args)
        {
            if (args.SocketError == SocketError.Success && args.BytesTransferred > 0)
            {
                //  disposing prevents memory leaks
                args.Dispose();
                asyncArgs = new SocketAsyncEventArgs();
                asyncArgs.SetBuffer(new byte[bufferSize], 0, bufferSize);
                asyncArgs.Completed += ProcessRecv;
                requestText += Encoding.UTF8.GetString(args.Buffer, 0, args.BytesTransferred);
                ProcessRequestText();
                if (!processingComplete)
                    SockRecv((Socket)sender);
            }
            else
            {
                //  error, connection broken
                processingComplete = true;
            }
        }

        /// <summary>
        /// Processes the request text received so far.
        /// </summary>
        void ProcessRequestText()
        {
            if (processingComplete)
                return;

            switch (processingState)
            {
                case ProcessingState.InitialLine:
                    {
                        var lfIndex = requestText.IndexOf("\n");
                        if (lfIndex > -1)
                        {
                            var initialLine = requestText.Substring(0, lfIndex + 1).Trim();
                            requestText = requestText.Substring(lfIndex + 1);
                            ParseInitialLine(initialLine);
                            if (!processingComplete)
                            {
                                processingState = ProcessingState.Headers;
                                ProcessRequestText();
                            }
                        }
                    }
                    break;
                case ProcessingState.Headers:
                    {
                        var lfIndex = requestText.IndexOf("\n");
                        while (lfIndex > -1)
                        {
                            var line = requestText.Substring(0, lfIndex + 1).Trim();
                            if (line == "")
                            {
                                processingState = ProcessingState.Body;
                                break;
                            }
                            requestText = requestText.Substring(lfIndex + 1);
                            ParseHeader(line);
                            if (processingComplete)
                                return;
                            lfIndex = requestText.IndexOf("\n");
                        }

                        if (processingState == ProcessingState.Body)
                        {
                            //  determine if a body should be read or not
                            if ((request.Method == HttpMethod.POST ||
                                request.Method == HttpMethod.PUT ||
                                request.Method == HttpMethod.PATCH) &&
                                request.Headers.ContainsKey("Content-Length"))
                            {
                                //  body required
                            }
                            else
                            {
                                //  done reading
                                processingComplete = true;
                                if (ReadComplete != null)
                                    ReadComplete(request);
                            }
                        }
                    }
                    break;
                case ProcessingState.Body:
                    {
                    }
                    break;
            }
        }

        void ParseInitialLine(string line)
        {
            var bits = line.Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length < 3)
                bits = line.Split("\t".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length < 3)
            {
                ParsingError(HttpStatusCode.BadRequest);
                return;
            }
            HttpMethod method;
            if (!Enum.TryParse<HttpMethod>(bits[0], true, out method))
            {
                ParsingError(HttpStatusCode.BadRequest);
                return;
            }
            request.Method = method;
            var uriStr = bits[1];
            if (!uriStr.StartsWith("http://") && !uriStr.StartsWith("https://"))
                uriStr = "http://localhost" + uriStr;
            request.Uri = new Uri(uriStr);
            if (bits[2] == "HTTP/1.0")
                request.Version = HttpVersion.Version10;
            else if (bits[2] == "HTTP/1.1")
                request.Version = HttpVersion.Version11;
            else
            {
                ParsingError(HttpStatusCode.BadRequest);
                return;
            }
        }

        void ParseHeader(string line)
        {
            var seperatorIndex = line.IndexOf(":");
            if (seperatorIndex > -1)
            {
                var header = line.Substring(0, seperatorIndex);
                var value = line.Substring(seperatorIndex + 1).Trim();
                if (!request.Headers.ContainsKey(header))
                    request.Headers.Add(header, value);
                else
                    request.Headers[header] = value;
            }
            else
            {
                ParsingError(HttpStatusCode.BadRequest);
            }
        }

        void ParsingError(HttpStatusCode httpStatusCode)
        {
            processingComplete = true;
            if (HttpError != null)
                HttpError(httpStatusCode, request);
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }

    enum ProcessingState
    {
        InitialLine,
        Headers,
        Body
    }
}
