/* Copyright 2013-present MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SAEA.Mongo.Bson;
using SAEA.Mongo.Bson.Serialization;
using SAEA.Mongo.Bson.Serialization.Serializers;
using SAEA.Mongo.Driver.Core.Bindings;
using SAEA.Mongo.Driver.Core.Connections;
using SAEA.Mongo.Driver.Core.Misc;
using SAEA.Mongo.Driver.Core.WireProtocol.Messages.Encoders;
using MongoDB.Shared;

namespace SAEA.Mongo.Driver.Core.Operations
{
    /// <summary>
    /// Represents a distinct operation.
    /// </summary>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    public class DistinctOperation<TValue> : IReadOperation<IAsyncCursor<TValue>>
    {
        // fields
        private Collation _collation;
        private CollectionNamespace _collectionNamespace;
        private BsonDocument _filter;
        private string _fieldName;
        private TimeSpan? _maxTime;
        private MessageEncoderSettings _messageEncoderSettings;
        private ReadConcern _readConcern = ReadConcern.Default;
        private bool _retryRequested;
        private IBsonSerializer<TValue> _valueSerializer;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="DistinctOperation{TValue}"/> class.
        /// </summary>
        /// <param name="collectionNamespace">The collection namespace.</param>
        /// <param name="valueSerializer">The value serializer.</param>
        /// <param name="fieldName">The name of the field.</param>
        /// <param name="messageEncoderSettings">The message encoder settings.</param>
        public DistinctOperation(CollectionNamespace collectionNamespace, IBsonSerializer<TValue> valueSerializer, string fieldName, MessageEncoderSettings messageEncoderSettings)
        {
            _collectionNamespace = Ensure.IsNotNull(collectionNamespace, nameof(collectionNamespace));
            _valueSerializer = Ensure.IsNotNull(valueSerializer, nameof(valueSerializer));
            _fieldName = Ensure.IsNotNullOrEmpty(fieldName, nameof(fieldName));
            _messageEncoderSettings = Ensure.IsNotNull(messageEncoderSettings, nameof(messageEncoderSettings));
        }

        // properties
        /// <summary>
        /// Gets or sets the collation.
        /// </summary>
        /// <value>
        /// The collation.
        /// </value>
        public Collation Collation
        {
            get { return _collation; }
            set { _collation = value; }
        }
        /// <summary>
        /// Gets the collection namespace.
        /// </summary>
        /// <value>
        /// The collection namespace.
        /// </value>
        public CollectionNamespace CollectionNamespace
        {
            get { return _collectionNamespace; }
        }

        /// <summary>
        /// Gets or sets the filter.
        /// </summary>
        /// <value>
        /// The filter.
        /// </value>
        public BsonDocument Filter
        {
            get { return _filter; }
            set { _filter = value; }
        }

        /// <summary>
        /// Gets the name of the field.
        /// </summary>
        /// <value>
        /// The name of the field.
        /// </value>
        public string FieldName
        {
            get { return _fieldName; }
        }

        /// <summary>
        /// Gets or sets the maximum time the server should spend on this operation.
        /// </summary>
        /// <value>
        /// The maximum time the server should spend on this operation.
        /// </value>
        public TimeSpan? MaxTime
        {
            get { return _maxTime; }
            set { _maxTime = Ensure.IsNullOrInfiniteOrGreaterThanOrEqualToZero(value, nameof(value)); }
        }

        /// <summary>
        /// Gets the message encoder settings.
        /// </summary>
        /// <value>
        /// The message encoder settings.
        /// </value>
        public MessageEncoderSettings MessageEncoderSettings
        {
            get { return _messageEncoderSettings; }
        }

        /// <summary>
        /// Gets or sets the read concern.
        /// </summary>
        /// <value>
        /// The read concern.
        /// </value>
        public ReadConcern ReadConcern
        {
            get { return _readConcern; }
            set { _readConcern = Ensure.IsNotNull(value, nameof(value)); }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to retry.
        /// </summary>
        /// <value>Whether to retry.</value>
        public bool RetryRequested
        {
            get => _retryRequested;
            set => _retryRequested = value;
        }

        /// <summary>
        /// Gets the value serializer.
        /// </summary>
        /// <value>
        /// The value serializer.
        /// </value>
        public IBsonSerializer<TValue> ValueSerializer
        {
            get { return _valueSerializer; }
        }

        // public methods
        /// <inheritdoc/>
        public IAsyncCursor<TValue> Execute(IReadBinding binding, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(binding, nameof(binding));

            using (var context = RetryableReadContext.Create(binding, _retryRequested, cancellationToken))
            {
                var operation = CreateOperation(context);
                var values = operation.Execute(context, cancellationToken);
                return new SingleBatchAsyncCursor<TValue>(values);
            }
        }

        /// <inheritdoc/>
        public async Task<IAsyncCursor<TValue>> ExecuteAsync(IReadBinding binding, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(binding, nameof(binding));

            using (var context = await RetryableReadContext.CreateAsync(binding, _retryRequested, cancellationToken).ConfigureAwait(false))
            {
                var operation = CreateOperation(context);
                var values = await operation.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                return new SingleBatchAsyncCursor<TValue>(values);
            }
        }

        // private methods
        internal BsonDocument CreateCommand(ConnectionDescription connectionDescription, ICoreSession session)
        {
            Feature.ReadConcern.ThrowIfNotSupported(connectionDescription.ServerVersion, _readConcern);
            Feature.Collation.ThrowIfNotSupported(connectionDescription.ServerVersion, _collation);

            var readConcern = ReadConcernHelper.GetReadConcernForCommand(session, connectionDescription, _readConcern);
            return new BsonDocument
            {
                { "distinct", _collectionNamespace.CollectionName },
                { "key", _fieldName },
                { "query", _filter, _filter != null },
                { "maxTimeMS", () => MaxTimeHelper.ToMaxTimeMS(_maxTime.Value), _maxTime.HasValue },
                { "collation", () => _collation.ToBsonDocument(), _collation != null },
                { "readConcern", readConcern, readConcern != null }
            };
        }

        private ReadCommandOperation<TValue[]> CreateOperation(RetryableReadContext context)
        {
            var command = CreateCommand(context.Channel.ConnectionDescription, context.Binding.Session);
            var valueArraySerializer = new ArraySerializer<TValue>(_valueSerializer);
            var resultSerializer = new ElementDeserializer<TValue[]>("values", valueArraySerializer);
            return new ReadCommandOperation<TValue[]>(_collectionNamespace.DatabaseNamespace, command, resultSerializer, _messageEncoderSettings)
            {
                RetryRequested = _retryRequested // might be overridden by retryable read context
            };
        }
    }
}