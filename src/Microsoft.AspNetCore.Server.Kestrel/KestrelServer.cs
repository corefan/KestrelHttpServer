// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Server.Kestrel
{
    public class KestrelServer : IServer
    {
        private Stack<IDisposable> _disposables;
        private readonly IApplicationLifetime _applicationLifetime;
        private readonly ILogger _logger;
        private readonly IServerAddressesFeature _serverAddresses;

        public KestrelServer(IOptions<KestrelServerOptions> options, IApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (applicationLifetime == null)
            {
                throw new ArgumentNullException(nameof(applicationLifetime));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            Options = options.Value ?? new KestrelServerOptions();
            _applicationLifetime = applicationLifetime;
            _logger = loggerFactory.CreateLogger(typeof(KestrelServer).GetTypeInfo().Namespace);
            Features = new FeatureCollection();
            _serverAddresses = new ServerAddressesFeature();
            Features.Set<IServerAddressesFeature>(_serverAddresses);
        }

        public IFeatureCollection Features { get; }

        public KestrelServerOptions Options { get; }

        public void Start<TContext>(IHttpApplication<TContext> application)
        {
            try
            {
                if (!BitConverter.IsLittleEndian)
                {
                    throw new PlatformNotSupportedException("Kestrel does not support big-endian architectures.");
                }

                ValidateOptions();

                if (_disposables != null)
                {
                    // The server has already started and/or has not been cleaned up yet
                    throw new InvalidOperationException("Server has already started.");
                }
                _disposables = new Stack<IDisposable>();

                var dateHeaderValueManager = new DateHeaderValueManager();
                var trace = new KestrelTrace(_logger);
                var engine = new KestrelEngine(new ServiceContext
                {
                    FrameFactory = context =>
                    {
                        return new Frame<TContext>(application, context);
                    },
                    AppLifetime = _applicationLifetime,
                    Log = trace,
                    ThreadPool = new LoggingThreadPool(trace),
                    DateHeaderValueManager = dateHeaderValueManager,
                    ServerOptions = Options
                });

                _disposables.Push(engine);
                _disposables.Push(dateHeaderValueManager);

                var threadCount = Options.ThreadCount;

                if (threadCount <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(threadCount),
                        threadCount,
                        "ThreadCount must be positive.");
                }

                if (!Constants.ECONNRESET.HasValue)
                {
                    _logger.LogWarning("Unable to determine ECONNRESET value on this platform.");
                }

                if (!Constants.EADDRINUSE.HasValue)
                {
                    _logger.LogWarning("Unable to determine EADDRINUSE value on this platform.");
                }

                engine.Start(threadCount);
                var atLeastOneListener = false;

                var listenOptions = Options.ListenOptions;

                if (listenOptions.Any())
                {
                    var addresses = _serverAddresses.Addresses;
                    if (addresses.SingleOrDefault() != "http://localhost:5000")
                    {
                        var joined = string.Join(", ", addresses);
                        _logger.LogWarning($"Address(es) '{joined}' were overridden. Binding to endpoints defined in UseKestrel instead.");
                    }

                    _serverAddresses.Addresses.Clear();
                }
                else
                {
                    // If no endpoints are configured directly using KestrelServerOptions, use those configured via --server.urls.
                    var copiedAddresses = _serverAddresses.Addresses.ToArray();
                    _serverAddresses.Addresses.Clear();

                    foreach (var address in copiedAddresses)
                    {
                        var parsedAddress = ServerAddress.FromUrl(address);

                        if (parsedAddress.IsUnixPipe)
                        {
                            listenOptions.Add(new ListenOptions(parsedAddress.UnixPipePath)
                            {
                                Scheme = parsedAddress.Scheme,
                                PathBase = parsedAddress.PathBase
                            });
                        }
                        else
                        {
                            if (string.Equals(parsedAddress.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                            {
                                // "localhost" for both IPv4 and IPv6 can't be represented as an IPEndPoint.
                                StartLocalhost(engine, parsedAddress);

                                // If StartLocalhost doesn't throw, there is at least one listener.
                                // The port cannot change for "localhost".
                                _serverAddresses.Addresses.Add(parsedAddress.ToString());
                                atLeastOneListener = true;
                            }
                            else
                            {
                                // These endPoints will be added later to _serverAddresses.Addresses
                                listenOptions.Add(new ListenOptions(CreateIPEndPoint(parsedAddress))
                                {
                                    Scheme = parsedAddress.Scheme,
                                    PathBase = parsedAddress.PathBase
                                });
                            }
                        }
                    }
                }

                foreach (var endPoint in listenOptions)
                {
                    atLeastOneListener = true;

                    try
                    {
                        _disposables.Push(engine.CreateServer(endPoint));
                    }
                    catch (AggregateException ex)
                    {
                        if ((ex.InnerException as UvException)?.StatusCode == Constants.EADDRINUSE)
                        {
                            throw new IOException($"Failed to bind to address {endPoint}: address already in use.", ex);
                        }

                        throw;
                    }

                    // If requested port was "0", replace with assigned dynamic port.
                    _serverAddresses.Addresses.Add(endPoint.ToString());
                }

                if (!atLeastOneListener)
                {
                    throw new InvalidOperationException("No recognized listening addresses were configured.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(0, ex, "Unable to start Kestrel.");
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposables != null)
            {
                while (_disposables.Count > 0)
                {
                    _disposables.Pop().Dispose();
                }
                _disposables = null;
            }
        }

        private void ValidateOptions()
        {
            if (Options.Limits.MaxRequestBufferSize.HasValue &&
                Options.Limits.MaxRequestBufferSize < Options.Limits.MaxRequestLineSize)
            {
                throw new InvalidOperationException(
                    $"Maximum request buffer size ({Options.Limits.MaxRequestBufferSize.Value}) must be greater than or equal to maximum request line size ({Options.Limits.MaxRequestLineSize}).");
            }
        }

        private void StartLocalhost(KestrelEngine engine, ServerAddress parsedAddress)
        {
            if (parsedAddress.Port == 0)
            {
                throw new InvalidOperationException("Dynamic port binding is not supported when binding to localhost. You must either bind to 127.0.0.1:0 or [::1]:0, or both.");
            }

            var exceptions = new List<Exception>();

            try
            {
                var ipv4ListenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, parsedAddress.Port))
                {
                    Scheme = parsedAddress.Scheme,
                    PathBase = parsedAddress.PathBase
                };

                _disposables.Push(engine.CreateServer(ipv4ListenOptions));
            }
            catch (AggregateException ex) when (ex.InnerException is UvException)
            {
                var uvEx = (UvException)ex.InnerException;
                if (uvEx.StatusCode == Constants.EADDRINUSE)
                {
                    throw new IOException($"Failed to bind to address {parsedAddress} on the IPv4 loopback interface: port already in use.", ex);
                }
                else
                {
                    _logger.LogWarning(0, $"Unable to bind to {parsedAddress} on the IPv4 loopback interface: ({uvEx.Message})");
                    exceptions.Add(uvEx);
                }
            }

            try
            {
                var ipv6ListenOptions = new ListenOptions(new IPEndPoint(IPAddress.IPv6Loopback, parsedAddress.Port))
                {
                    Scheme = parsedAddress.Scheme,
                    PathBase = parsedAddress.PathBase
                };

                _disposables.Push(engine.CreateServer(ipv6ListenOptions));
            }
            catch (AggregateException ex) when (ex.InnerException is UvException)
            {
                var uvEx = (UvException)ex.InnerException;
                if (uvEx.StatusCode == Constants.EADDRINUSE)
                {
                    throw new IOException($"Failed to bind to address {parsedAddress} on the IPv6 loopback interface: port already in use.", ex);
                }
                else
                {
                    _logger.LogWarning(0, $"Unable to bind to {parsedAddress} on the IPv6 loopback interface: ({uvEx.Message})");
                    exceptions.Add(uvEx);
                }
            }

            if (exceptions.Count == 2)
            {
                throw new IOException($"Failed to bind to address {parsedAddress}.", new AggregateException(exceptions));
            }
        }

        /// <summary>
        /// Returns an <see cref="IPEndPoint"/> for the given host an port.
        /// If the host parameter isn't "localhost" or an IP address, use IPAddress.Any.
        /// </summary>
        internal static IPEndPoint CreateIPEndPoint(ServerAddress address)
        {
            IPAddress ip;

            if (!IPAddress.TryParse(address.Host, out ip))
            {
                ip = IPAddress.IPv6Any;
            }

            return new IPEndPoint(ip, address.Port);
        }
    }
}
