﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Routing;
using Microsoft.AspNet.OData.Adapters;
using Microsoft.AspNet.OData.Batch;
using Microsoft.AspNet.OData.Common;
using Microsoft.AspNet.OData.Extensions;
using Microsoft.AspNet.OData.Formatter.Deserialization;
using Microsoft.AspNet.OData.Formatter.Serialization;
using Microsoft.AspNet.OData.Routing;
using Microsoft.OData;

namespace Microsoft.AspNet.OData.Formatter
{
    /// <summary>
    /// <see cref="MediaTypeFormatter"/> class to handle OData.
    /// </summary>
    [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Class coupling acceptable")]
    public class ODataMediaTypeFormatter : MediaTypeFormatter
    {
        private readonly ODataVersion _version;

        private readonly IEnumerable<ODataPayloadKind> _payloadKinds;
        private readonly ODataDeserializerProvider _deserializerProvider;
        private readonly ODataSerializerProvider _serializerProvider;

        private HttpRequestMessage _request;

        /// <summary>
        /// Initializes a new instance of the <see cref="ODataMediaTypeFormatter"/> class.
        /// </summary>
        /// <param name="payloadKinds">The kind of payloads this formatter supports.</param>
        public ODataMediaTypeFormatter(IEnumerable<ODataPayloadKind> payloadKinds)
            : this(ODataDeserializerProviderProxy.Instance, ODataSerializerProviderProxy.Instance, payloadKinds)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ODataMediaTypeFormatter"/> class.
        /// </summary>
        /// <param name="deserializerProvider">The <see cref="ODataDeserializerProvider"/> to use.</param>
        /// <param name="serializerProvider">The <see cref="ODataSerializerProvider"/> to use.</param>
        /// <param name="payloadKinds">The kind of payloads this formatter supports.</param>
        public ODataMediaTypeFormatter(ODataDeserializerProvider deserializerProvider, ODataSerializerProvider serializerProvider,
            IEnumerable<ODataPayloadKind> payloadKinds)
        {
            if (deserializerProvider == null)
            {
                throw Error.ArgumentNull("deserializerProvider");
            }
            if (serializerProvider == null)
            {
                throw Error.ArgumentNull("serializerProvider");
            }
            if (payloadKinds == null)
            {
                throw Error.ArgumentNull("payloadKinds");
            }

            _deserializerProvider = deserializerProvider;
            _serializerProvider = serializerProvider;
            _payloadKinds = payloadKinds;

            _version = ODataVersionConstraint.DefaultODataVersion;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ODataMediaTypeFormatter"/> class.
        /// </summary>
        /// <param name="formatter">The <see cref="ODataMediaTypeFormatter"/> to copy settings from.</param>
        /// <param name="version">The OData version that this formatter supports.</param>
        /// <param name="request">The <see cref="HttpRequestMessage"/> for the per-request formatter instance.</param>
        /// <remarks>This is a copy constructor to be used in <see cref="GetPerRequestFormatterInstance"/>.</remarks>
        internal ODataMediaTypeFormatter(ODataMediaTypeFormatter formatter, ODataVersion version, HttpRequestMessage request)
            : base(formatter)
        {
            if (request == null)
            {
                throw Error.ArgumentNull("request");
            }

            Contract.Assert(formatter._deserializerProvider != null);
            Contract.Assert(formatter._serializerProvider != null);
            Contract.Assert(formatter._payloadKinds != null);

            // Parameter 1: formatter

            // Except for the other two parameters, this constructor is a copy constructor, and we need to copy
            // everything on the other instance.

            // Copy this class's private fields and internal properties.
            _deserializerProvider = formatter._deserializerProvider;
            _serializerProvider = formatter._serializerProvider;
            _payloadKinds = formatter._payloadKinds;

            // Parameter 2: version
            _version = version;

            // Parameter 3: request
            Request = request;
        }

        /// <summary>
        /// Gets the <see cref="ODataSerializerProvider"/> that will be used by this formatter instance.
        /// </summary>
        public ODataSerializerProvider SerializerProvider
        {
            get
            {
                return _serializerProvider;
            }
        }

        /// <summary>
        /// Gets the <see cref="ODataDeserializerProvider"/> that will be used by this formatter instance.
        /// </summary>
        public ODataDeserializerProvider DeserializerProvider
        {
            get
            {
                return _deserializerProvider;
            }
        }

        /// <summary>
        /// Gets or sets a method that allows consumers to provide an alternate base
        /// address for OData Uri.
        /// </summary>
        public Func<HttpRequestMessage, Uri> BaseAddressFactory { get; set; }

        /// <summary>
        /// The request message associated with the per-request formatter instance.
        /// </summary>
        public HttpRequestMessage Request
        {
            get { return _request; }
            set
            {
                EnsureRequestContainer(value);
                _request = value;
            }
        }

        /// <inheritdoc/>
        public override MediaTypeFormatter GetPerRequestFormatterInstance(Type type, HttpRequestMessage request, MediaTypeHeaderValue mediaType)
        {
            // call base to validate parameters
            base.GetPerRequestFormatterInstance(type, request, mediaType);

            if (Request != null && Request == request)
            {
                // If the request is already set on this formatter, return itself.
                return this;
            }
            else
            {
                ODataVersion version = GetODataResponseVersion(request);
                return new ODataMediaTypeFormatter(this, version, request);
            }
        }

        /// <inheritdoc/>
        public override void SetDefaultContentHeaders(Type type, HttpContentHeaders headers, MediaTypeHeaderValue mediaType)
        {
            // Determine the content type or let base class handle it.
            MediaTypeHeaderValue newMediaType = null;
            if (ODataOutputFormatterHelper.TryGetContentHeader(type, mediaType, out newMediaType))
            {
                headers.ContentType = newMediaType;
            }
            else
            {
                // This is the case when a user creates a new ObjectContent<T> passing in a null mediaType
                base.SetDefaultContentHeaders(type, headers, mediaType);
            }

            // Set the character set.
            IEnumerable<string> acceptCharsetValues = Request.Headers.AcceptCharset.Select(cs => cs.Value);

            string newCharSet = String.Empty;
            if (ODataOutputFormatterHelper.TryGetCharSet(headers.ContentType, acceptCharsetValues, out newCharSet))
            {
                headers.ContentType.CharSet = newCharSet;
            }

            // Add version header.
            headers.TryAddWithoutValidation(
                ODataVersionConstraint.ODataServiceVersionHeader,
                ODataUtils.ODataVersionToString(_version));
        }

        /// <inheritdoc/>
        public override bool CanReadType(Type type)
        {
            if (type == null)
            {
                throw Error.ArgumentNull("type");
            }

            if (Request != null)
            {
                return ODataInputFormatterHelper.CanReadType(
                    type,
                    Request.GetModel(),
                    Request.ODataProperties().Path,
                    _payloadKinds,
                    (objectType) => _deserializerProvider.GetEdmTypeDeserializer(objectType),
                    (objectType) => _deserializerProvider.GetODataDeserializer(objectType, Request));
            }

            return false;
        }

        /// <inheritdoc/>
        public override bool CanWriteType(Type type)
        {
            if (type == null)
            {
                throw Error.ArgumentNull("type");
            }

            if (Request != null)
            {
                return ODataOutputFormatterHelper.CanWriteType(
                    type,
                    _payloadKinds,
                    type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SingleResult<>),
                    new WebApiRequestMessage(Request),
                    (objectType) => _serializerProvider.GetODataPayloadSerializer(objectType, Request));
            }

            return false;
        }

        /// <inheritdoc/>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "The caught exception type is reflected into a faulted task.")]
        [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Class coupling results for making ReadFromStream platform-agnostic.")]
        public override Task<object> ReadFromStreamAsync(Type type, Stream readStream, HttpContent content, IFormatterLogger formatterLogger)
        {
            if (type == null)
            {
                throw Error.ArgumentNull("type");
            }

            if (readStream == null)
            {
                throw Error.ArgumentNull("readStream");
            }

            if (Request == null)
            {
                throw Error.InvalidOperation(SRResources.ReadFromStreamAsyncMustHaveRequest);
            }

            object defaultValue = GetDefaultValueForType(type);

            // If content length is 0 then return default value for this type
            HttpContentHeaders contentHeaders = (content == null) ? null : content.Headers;
            if (contentHeaders == null || contentHeaders.ContentLength == 0)
            {
                return Task.FromResult(defaultValue);
            }

            try
            {
                Func<ODataDeserializerContext> getODataDeserializerContext = () =>
                {
                    return new ODataDeserializerContext
                    {
                        Request = Request,
                    };
                };

                Action<Exception> logErrorAction = (ex) =>
                {
                    if (formatterLogger == null)
                    {
                        throw ex;
                    }

                    formatterLogger.LogError(String.Empty, ex);
                };

                return Task.FromResult(ODataInputFormatterHelper.ReadFromStream(
                    type,
                    defaultValue,
                    Request.GetModel(),
                    GetBaseAddressInternal(Request),
                    new WebApiRequestMessage(Request),
                    () => ODataMessageWrapperHelper.Create(readStream, contentHeaders, Request.GetODataContentIdMapping(), Request.GetRequestContainer()),
                    (objectType) => _deserializerProvider.GetEdmTypeDeserializer(objectType),
                    (objectType) => _deserializerProvider.GetODataDeserializer(objectType, Request),
                    getODataDeserializerContext,
                    (disposable) => Request.RegisterForDispose(disposable),
                    logErrorAction));
            }
            catch (Exception ex)
            {
                return TaskHelpers.FromError<object>(ex);
            }
        }

        /// <inheritdoc/>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "The caught exception type is reflected into a faulted task.")]
        [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Class coupling results for making WriteToStream platform-agnostic.")]
        public override Task WriteToStreamAsync(Type type, object value, Stream writeStream, HttpContent content,
            TransportContext transportContext, CancellationToken cancellationToken)
        {
            if (type == null)
            {
                throw Error.ArgumentNull("type");
            }
            if (writeStream == null)
            {
                throw Error.ArgumentNull("writeStream");
            }
            if (Request == null)
            {
                throw Error.InvalidOperation(SRResources.WriteToStreamAsyncMustHaveRequest);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return TaskHelpers.Canceled();
            }

            try
            {
                HttpConfiguration configuration = Request.GetConfiguration();
                if (configuration == null)
                {
                    throw Error.InvalidOperation(SRResources.RequestMustContainConfiguration);
                }

                HttpContentHeaders contentHeaders = (content == null) ? null : content.Headers;
                UrlHelper urlHelper = Request.GetUrlHelper() ?? new UrlHelper(Request);

                Func<ODataSerializerContext> getODataSerializerContext = () =>
                {
                    return new ODataSerializerContext()
                    {
                        Request = Request,
                        Url = urlHelper,
                    };
                };

                ODataOutputFormatterHelper.WriteToStream(
                    type,
                    value,
                    Request.GetModel(),
                    _version,
                    GetBaseAddressInternal(Request),
                    contentHeaders == null ? null : contentHeaders.ContentType,
                    new WebApiUrlHelper(urlHelper),
                    new WebApiRequestMessage(Request),
                    new WebApiRequestHeaders(Request.Headers),
                    (services) => ODataMessageWrapperHelper.Create(writeStream, contentHeaders, services),
                    (edmType) => _serializerProvider.GetEdmTypeSerializer(edmType),
                    (objectType) => _serializerProvider.GetODataPayloadSerializer(objectType, Request),
                    getODataSerializerContext);

                return TaskHelpers.Completed();
            }
            catch (Exception ex)
            {
                return TaskHelpers.FromError(ex);
            }
        }

        // To factor out request, just pass in a function to get base address. We'd get rid of
        // BaseAddressFactory and request.
        private Uri GetBaseAddressInternal(HttpRequestMessage request)
        {
            if (BaseAddressFactory != null)
            {
                return BaseAddressFactory(request);
            }
            else
            {
                return ODataMediaTypeFormatter.GetDefaultBaseAddress(request);
            }
        }

        /// <summary>
        /// Returns a base address to be used in the service root when reading or writing OData uris.
        /// </summary>
        /// <param name="request">The HttpRequestMessage object for the given request.</param>
        /// <returns>The base address to be used as part of the service root in the OData uri; must terminate with a trailing '/'.</returns>
        public static Uri GetDefaultBaseAddress(HttpRequestMessage request)
        {
            if (request == null)
            {
                throw Error.ArgumentNull("request");
            }

            UrlHelper urlHelper = request.GetUrlHelper() ?? new UrlHelper(request);

            string baseAddress = urlHelper.CreateODataLink();
            if (baseAddress == null)
            {
                throw new SerializationException(SRResources.UnableToDetermineBaseUrl);
            }

            return baseAddress[baseAddress.Length - 1] != '/' ? new Uri(baseAddress + '/') : new Uri(baseAddress);
        }

        internal static ODataVersion GetODataResponseVersion(HttpRequestMessage request)
        {
            // OData protocol requires that you send the minimum version that the client needs to know to
            // understand the response. There is no easy way we can figure out the minimum version that the client
            // needs to understand our response. We send response headers much ahead generating the response. So if
            // the requestMessage has a OData-MaxVersion, tell the client that our response is of the same
            // version; else use the DataServiceVersionHeader. Our response might require a higher version of the
            // client and it might fail. If the client doesn't send these headers respond with the default version
            // (V4).
            HttpRequestMessageProperties properties = request.ODataProperties();
            return properties.ODataMaxServiceVersion ??
                properties.ODataServiceVersion ??
                ODataVersionConstraint.DefaultODataVersion;
        }

        private void EnsureRequestContainer(HttpRequestMessage request)
        {
            ODataSerializerProviderProxy serializerProviderProxy = _serializerProvider as ODataSerializerProviderProxy;
            if (serializerProviderProxy != null && serializerProviderProxy.RequestContainer == null)
            {
                serializerProviderProxy.RequestContainer = request.GetRequestContainer();
            }

            ODataDeserializerProviderProxy deserializerProviderProxy = _deserializerProvider as ODataDeserializerProviderProxy;
            if (deserializerProviderProxy != null && deserializerProviderProxy.RequestContainer == null)
            {
                deserializerProviderProxy.RequestContainer = request.GetRequestContainer();
            }
        }
    }
}