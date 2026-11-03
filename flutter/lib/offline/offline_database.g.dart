// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'offline_database.dart';

// ignore_for_file: type=lint
class $CachedResponsesTable extends CachedResponses
    with TableInfo<$CachedResponsesTable, CachedResponse> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $CachedResponsesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _cacheKeyMeta =
      const VerificationMeta('cacheKey');
  @override
  late final GeneratedColumn<String> cacheKey = GeneratedColumn<String>(
      'cache_key', aliasedName, false,
      type: DriftSqlType.string, requiredDuringInsert: true);
  static const VerificationMeta _endpointMeta =
      const VerificationMeta('endpoint');
  @override
  late final GeneratedColumn<String> endpoint = GeneratedColumn<String>(
      'endpoint', aliasedName, false,
      type: DriftSqlType.string, requiredDuringInsert: true);
  static const VerificationMeta _payloadMeta =
      const VerificationMeta('payload');
  @override
  late final GeneratedColumn<String> payload = GeneratedColumn<String>(
      'payload', aliasedName, false,
      type: DriftSqlType.string, requiredDuringInsert: true);
  static const VerificationMeta _fetchedAtMeta =
      const VerificationMeta('fetchedAt');
  @override
  late final GeneratedColumn<DateTime> fetchedAt = GeneratedColumn<DateTime>(
      'fetched_at', aliasedName, false,
      type: DriftSqlType.dateTime, requiredDuringInsert: true);
  @override
  List<GeneratedColumn> get $columns =>
      [cacheKey, endpoint, payload, fetchedAt];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'cached_responses';
  @override
  VerificationContext validateIntegrity(Insertable<CachedResponse> instance,
      {bool isInserting = false}) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('cache_key')) {
      context.handle(_cacheKeyMeta,
          cacheKey.isAcceptableOrUnknown(data['cache_key']!, _cacheKeyMeta));
    } else if (isInserting) {
      context.missing(_cacheKeyMeta);
    }
    if (data.containsKey('endpoint')) {
      context.handle(_endpointMeta,
          endpoint.isAcceptableOrUnknown(data['endpoint']!, _endpointMeta));
    } else if (isInserting) {
      context.missing(_endpointMeta);
    }
    if (data.containsKey('payload')) {
      context.handle(_payloadMeta,
          payload.isAcceptableOrUnknown(data['payload']!, _payloadMeta));
    } else if (isInserting) {
      context.missing(_payloadMeta);
    }
    if (data.containsKey('fetched_at')) {
      context.handle(_fetchedAtMeta,
          fetchedAt.isAcceptableOrUnknown(data['fetched_at']!, _fetchedAtMeta));
    } else if (isInserting) {
      context.missing(_fetchedAtMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {cacheKey};
  @override
  CachedResponse map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return CachedResponse(
      cacheKey: attachedDatabase.typeMapping
          .read(DriftSqlType.string, data['${effectivePrefix}cache_key'])!,
      endpoint: attachedDatabase.typeMapping
          .read(DriftSqlType.string, data['${effectivePrefix}endpoint'])!,
      payload: attachedDatabase.typeMapping
          .read(DriftSqlType.string, data['${effectivePrefix}payload'])!,
      fetchedAt: attachedDatabase.typeMapping
          .read(DriftSqlType.dateTime, data['${effectivePrefix}fetched_at'])!,
    );
  }

  @override
  $CachedResponsesTable createAlias(String alias) {
    return $CachedResponsesTable(attachedDatabase, alias);
  }
}

class CachedResponse extends DataClass implements Insertable<CachedResponse> {
  final String cacheKey;
  final String endpoint;
  final String payload;
  final DateTime fetchedAt;
  const CachedResponse(
      {required this.cacheKey,
      required this.endpoint,
      required this.payload,
      required this.fetchedAt});
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['cache_key'] = Variable<String>(cacheKey);
    map['endpoint'] = Variable<String>(endpoint);
    map['payload'] = Variable<String>(payload);
    map['fetched_at'] = Variable<DateTime>(fetchedAt);
    return map;
  }

  CachedResponsesCompanion toCompanion(bool nullToAbsent) {
    return CachedResponsesCompanion(
      cacheKey: Value(cacheKey),
      endpoint: Value(endpoint),
      payload: Value(payload),
      fetchedAt: Value(fetchedAt),
    );
  }

  factory CachedResponse.fromJson(Map<String, dynamic> json,
      {ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return CachedResponse(
      cacheKey: serializer.fromJson<String>(json['cacheKey']),
      endpoint: serializer.fromJson<String>(json['endpoint']),
      payload: serializer.fromJson<String>(json['payload']),
      fetchedAt: serializer.fromJson<DateTime>(json['fetchedAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'cacheKey': serializer.toJson<String>(cacheKey),
      'endpoint': serializer.toJson<String>(endpoint),
      'payload': serializer.toJson<String>(payload),
      'fetchedAt': serializer.toJson<DateTime>(fetchedAt),
    };
  }

  CachedResponse copyWith(
          {String? cacheKey,
          String? endpoint,
          String? payload,
          DateTime? fetchedAt}) =>
      CachedResponse(
        cacheKey: cacheKey ?? this.cacheKey,
        endpoint: endpoint ?? this.endpoint,
        payload: payload ?? this.payload,
        fetchedAt: fetchedAt ?? this.fetchedAt,
      );
  CachedResponse copyWithCompanion(CachedResponsesCompanion data) {
    return CachedResponse(
      cacheKey: data.cacheKey.present ? data.cacheKey.value : this.cacheKey,
      endpoint: data.endpoint.present ? data.endpoint.value : this.endpoint,
      payload: data.payload.present ? data.payload.value : this.payload,
      fetchedAt: data.fetchedAt.present ? data.fetchedAt.value : this.fetchedAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('CachedResponse(')
          ..write('cacheKey: $cacheKey, ')
          ..write('endpoint: $endpoint, ')
          ..write('payload: $payload, ')
          ..write('fetchedAt: $fetchedAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(cacheKey, endpoint, payload, fetchedAt);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is CachedResponse &&
          other.cacheKey == this.cacheKey &&
          other.endpoint == this.endpoint &&
          other.payload == this.payload &&
          other.fetchedAt == this.fetchedAt);
}

class CachedResponsesCompanion extends UpdateCompanion<CachedResponse> {
  final Value<String> cacheKey;
  final Value<String> endpoint;
  final Value<String> payload;
  final Value<DateTime> fetchedAt;
  final Value<int> rowid;
  const CachedResponsesCompanion({
    this.cacheKey = const Value.absent(),
    this.endpoint = const Value.absent(),
    this.payload = const Value.absent(),
    this.fetchedAt = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  CachedResponsesCompanion.insert({
    required String cacheKey,
    required String endpoint,
    required String payload,
    required DateTime fetchedAt,
    this.rowid = const Value.absent(),
  })  : cacheKey = Value(cacheKey),
        endpoint = Value(endpoint),
        payload = Value(payload),
        fetchedAt = Value(fetchedAt);
  static Insertable<CachedResponse> custom({
    Expression<String>? cacheKey,
    Expression<String>? endpoint,
    Expression<String>? payload,
    Expression<DateTime>? fetchedAt,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (cacheKey != null) 'cache_key': cacheKey,
      if (endpoint != null) 'endpoint': endpoint,
      if (payload != null) 'payload': payload,
      if (fetchedAt != null) 'fetched_at': fetchedAt,
      if (rowid != null) 'rowid': rowid,
    });
  }

  CachedResponsesCompanion copyWith(
      {Value<String>? cacheKey,
      Value<String>? endpoint,
      Value<String>? payload,
      Value<DateTime>? fetchedAt,
      Value<int>? rowid}) {
    return CachedResponsesCompanion(
      cacheKey: cacheKey ?? this.cacheKey,
      endpoint: endpoint ?? this.endpoint,
      payload: payload ?? this.payload,
      fetchedAt: fetchedAt ?? this.fetchedAt,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (cacheKey.present) {
      map['cache_key'] = Variable<String>(cacheKey.value);
    }
    if (endpoint.present) {
      map['endpoint'] = Variable<String>(endpoint.value);
    }
    if (payload.present) {
      map['payload'] = Variable<String>(payload.value);
    }
    if (fetchedAt.present) {
      map['fetched_at'] = Variable<DateTime>(fetchedAt.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('CachedResponsesCompanion(')
          ..write('cacheKey: $cacheKey, ')
          ..write('endpoint: $endpoint, ')
          ..write('payload: $payload, ')
          ..write('fetchedAt: $fetchedAt, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $OutboxEntriesTable extends OutboxEntries
    with TableInfo<$OutboxEntriesTable, OutboxEntry> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $OutboxEntriesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _idMeta = const VerificationMeta('id');
  @override
  late final GeneratedColumn<int> id = GeneratedColumn<int>(
      'id', aliasedName, false,
      hasAutoIncrement: true,
      type: DriftSqlType.int,
      requiredDuringInsert: false,
      defaultConstraints:
          GeneratedColumn.constraintIsAlways('PRIMARY KEY AUTOINCREMENT'));
  static const VerificationMeta _idempotencyKeyMeta =
      const VerificationMeta('idempotencyKey');
  @override
  late final GeneratedColumn<String> idempotencyKey = GeneratedColumn<String>(
      'idempotency_key', aliasedName, false,
      type: DriftSqlType.string,
      requiredDuringInsert: true,
      defaultConstraints: GeneratedColumn.constraintIsAlways('UNIQUE'));
  static const VerificationMeta _operationTypeMeta =
      const VerificationMeta('operationType');
  @override
  late final GeneratedColumn<String> operationType = GeneratedColumn<String>(
      'operation_type', aliasedName, false,
      type: DriftSqlType.string, requiredDuringInsert: true);
  static const VerificationMeta _entityIdMeta =
      const VerificationMeta('entityId');
  @override
  late final GeneratedColumn<String> entityId = GeneratedColumn<String>(
      'entity_id', aliasedName, false,
      type: DriftSqlType.string, requiredDuringInsert: true);
  static const VerificationMeta _payloadMeta =
      const VerificationMeta('payload');
  @override
  late final GeneratedColumn<String> payload = GeneratedColumn<String>(
      'payload', aliasedName, false,
      type: DriftSqlType.string, requiredDuringInsert: true);
  static const VerificationMeta _queuedAtMeta =
      const VerificationMeta('queuedAt');
  @override
  late final GeneratedColumn<DateTime> queuedAt = GeneratedColumn<DateTime>(
      'queued_at', aliasedName, false,
      type: DriftSqlType.dateTime, requiredDuringInsert: true);
  static const VerificationMeta _baseUpdatedAtMeta =
      const VerificationMeta('baseUpdatedAt');
  @override
  late final GeneratedColumn<DateTime> baseUpdatedAt =
      GeneratedColumn<DateTime>('base_updated_at', aliasedName, true,
          type: DriftSqlType.dateTime, requiredDuringInsert: false);
  static const VerificationMeta _retryCountMeta =
      const VerificationMeta('retryCount');
  @override
  late final GeneratedColumn<int> retryCount = GeneratedColumn<int>(
      'retry_count', aliasedName, false,
      type: DriftSqlType.int,
      requiredDuringInsert: false,
      defaultValue: const Constant(0));
  static const VerificationMeta _lastAttemptAtMeta =
      const VerificationMeta('lastAttemptAt');
  @override
  late final GeneratedColumn<DateTime> lastAttemptAt =
      GeneratedColumn<DateTime>('last_attempt_at', aliasedName, true,
          type: DriftSqlType.dateTime, requiredDuringInsert: false);
  @override
  List<GeneratedColumn> get $columns => [
        id,
        idempotencyKey,
        operationType,
        entityId,
        payload,
        queuedAt,
        baseUpdatedAt,
        retryCount,
        lastAttemptAt
      ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'outbox_entries';
  @override
  VerificationContext validateIntegrity(Insertable<OutboxEntry> instance,
      {bool isInserting = false}) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('id')) {
      context.handle(_idMeta, id.isAcceptableOrUnknown(data['id']!, _idMeta));
    }
    if (data.containsKey('idempotency_key')) {
      context.handle(
          _idempotencyKeyMeta,
          idempotencyKey.isAcceptableOrUnknown(
              data['idempotency_key']!, _idempotencyKeyMeta));
    } else if (isInserting) {
      context.missing(_idempotencyKeyMeta);
    }
    if (data.containsKey('operation_type')) {
      context.handle(
          _operationTypeMeta,
          operationType.isAcceptableOrUnknown(
              data['operation_type']!, _operationTypeMeta));
    } else if (isInserting) {
      context.missing(_operationTypeMeta);
    }
    if (data.containsKey('entity_id')) {
      context.handle(_entityIdMeta,
          entityId.isAcceptableOrUnknown(data['entity_id']!, _entityIdMeta));
    } else if (isInserting) {
      context.missing(_entityIdMeta);
    }
    if (data.containsKey('payload')) {
      context.handle(_payloadMeta,
          payload.isAcceptableOrUnknown(data['payload']!, _payloadMeta));
    } else if (isInserting) {
      context.missing(_payloadMeta);
    }
    if (data.containsKey('queued_at')) {
      context.handle(_queuedAtMeta,
          queuedAt.isAcceptableOrUnknown(data['queued_at']!, _queuedAtMeta));
    } else if (isInserting) {
      context.missing(_queuedAtMeta);
    }
    if (data.containsKey('base_updated_at')) {
      context.handle(
          _baseUpdatedAtMeta,
          baseUpdatedAt.isAcceptableOrUnknown(
              data['base_updated_at']!, _baseUpdatedAtMeta));
    }
    if (data.containsKey('retry_count')) {
      context.handle(
          _retryCountMeta,
          retryCount.isAcceptableOrUnknown(
              data['retry_count']!, _retryCountMeta));
    }
    if (data.containsKey('last_attempt_at')) {
      context.handle(
          _lastAttemptAtMeta,
          lastAttemptAt.isAcceptableOrUnknown(
              data['last_attempt_at']!, _lastAttemptAtMeta));
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {id};
  @override
  OutboxEntry map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return OutboxEntry(
      id: attachedDatabase.typeMapping
          .read(DriftSqlType.int, data['${effectivePrefix}id'])!,
      idempotencyKey: attachedDatabase.typeMapping.read(
          DriftSqlType.string, data['${effectivePrefix}idempotency_key'])!,
      operationType: attachedDatabase.typeMapping
          .read(DriftSqlType.string, data['${effectivePrefix}operation_type'])!,
      entityId: attachedDatabase.typeMapping
          .read(DriftSqlType.string, data['${effectivePrefix}entity_id'])!,
      payload: attachedDatabase.typeMapping
          .read(DriftSqlType.string, data['${effectivePrefix}payload'])!,
      queuedAt: attachedDatabase.typeMapping
          .read(DriftSqlType.dateTime, data['${effectivePrefix}queued_at'])!,
      baseUpdatedAt: attachedDatabase.typeMapping.read(
          DriftSqlType.dateTime, data['${effectivePrefix}base_updated_at']),
      retryCount: attachedDatabase.typeMapping
          .read(DriftSqlType.int, data['${effectivePrefix}retry_count'])!,
      lastAttemptAt: attachedDatabase.typeMapping.read(
          DriftSqlType.dateTime, data['${effectivePrefix}last_attempt_at']),
    );
  }

  @override
  $OutboxEntriesTable createAlias(String alias) {
    return $OutboxEntriesTable(attachedDatabase, alias);
  }
}

class OutboxEntry extends DataClass implements Insertable<OutboxEntry> {
  final int id;
  final String idempotencyKey;
  final String operationType;
  final String entityId;
  final String payload;
  final DateTime queuedAt;
  final DateTime? baseUpdatedAt;
  final int retryCount;
  final DateTime? lastAttemptAt;
  const OutboxEntry(
      {required this.id,
      required this.idempotencyKey,
      required this.operationType,
      required this.entityId,
      required this.payload,
      required this.queuedAt,
      this.baseUpdatedAt,
      required this.retryCount,
      this.lastAttemptAt});
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['id'] = Variable<int>(id);
    map['idempotency_key'] = Variable<String>(idempotencyKey);
    map['operation_type'] = Variable<String>(operationType);
    map['entity_id'] = Variable<String>(entityId);
    map['payload'] = Variable<String>(payload);
    map['queued_at'] = Variable<DateTime>(queuedAt);
    if (!nullToAbsent || baseUpdatedAt != null) {
      map['base_updated_at'] = Variable<DateTime>(baseUpdatedAt);
    }
    map['retry_count'] = Variable<int>(retryCount);
    if (!nullToAbsent || lastAttemptAt != null) {
      map['last_attempt_at'] = Variable<DateTime>(lastAttemptAt);
    }
    return map;
  }

  OutboxEntriesCompanion toCompanion(bool nullToAbsent) {
    return OutboxEntriesCompanion(
      id: Value(id),
      idempotencyKey: Value(idempotencyKey),
      operationType: Value(operationType),
      entityId: Value(entityId),
      payload: Value(payload),
      queuedAt: Value(queuedAt),
      baseUpdatedAt: baseUpdatedAt == null && nullToAbsent
          ? const Value.absent()
          : Value(baseUpdatedAt),
      retryCount: Value(retryCount),
      lastAttemptAt: lastAttemptAt == null && nullToAbsent
          ? const Value.absent()
          : Value(lastAttemptAt),
    );
  }

  factory OutboxEntry.fromJson(Map<String, dynamic> json,
      {ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return OutboxEntry(
      id: serializer.fromJson<int>(json['id']),
      idempotencyKey: serializer.fromJson<String>(json['idempotencyKey']),
      operationType: serializer.fromJson<String>(json['operationType']),
      entityId: serializer.fromJson<String>(json['entityId']),
      payload: serializer.fromJson<String>(json['payload']),
      queuedAt: serializer.fromJson<DateTime>(json['queuedAt']),
      baseUpdatedAt: serializer.fromJson<DateTime?>(json['baseUpdatedAt']),
      retryCount: serializer.fromJson<int>(json['retryCount']),
      lastAttemptAt: serializer.fromJson<DateTime?>(json['lastAttemptAt']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'id': serializer.toJson<int>(id),
      'idempotencyKey': serializer.toJson<String>(idempotencyKey),
      'operationType': serializer.toJson<String>(operationType),
      'entityId': serializer.toJson<String>(entityId),
      'payload': serializer.toJson<String>(payload),
      'queuedAt': serializer.toJson<DateTime>(queuedAt),
      'baseUpdatedAt': serializer.toJson<DateTime?>(baseUpdatedAt),
      'retryCount': serializer.toJson<int>(retryCount),
      'lastAttemptAt': serializer.toJson<DateTime?>(lastAttemptAt),
    };
  }

  OutboxEntry copyWith(
          {int? id,
          String? idempotencyKey,
          String? operationType,
          String? entityId,
          String? payload,
          DateTime? queuedAt,
          Value<DateTime?> baseUpdatedAt = const Value.absent(),
          int? retryCount,
          Value<DateTime?> lastAttemptAt = const Value.absent()}) =>
      OutboxEntry(
        id: id ?? this.id,
        idempotencyKey: idempotencyKey ?? this.idempotencyKey,
        operationType: operationType ?? this.operationType,
        entityId: entityId ?? this.entityId,
        payload: payload ?? this.payload,
        queuedAt: queuedAt ?? this.queuedAt,
        baseUpdatedAt:
            baseUpdatedAt.present ? baseUpdatedAt.value : this.baseUpdatedAt,
        retryCount: retryCount ?? this.retryCount,
        lastAttemptAt:
            lastAttemptAt.present ? lastAttemptAt.value : this.lastAttemptAt,
      );
  OutboxEntry copyWithCompanion(OutboxEntriesCompanion data) {
    return OutboxEntry(
      id: data.id.present ? data.id.value : this.id,
      idempotencyKey: data.idempotencyKey.present
          ? data.idempotencyKey.value
          : this.idempotencyKey,
      operationType: data.operationType.present
          ? data.operationType.value
          : this.operationType,
      entityId: data.entityId.present ? data.entityId.value : this.entityId,
      payload: data.payload.present ? data.payload.value : this.payload,
      queuedAt: data.queuedAt.present ? data.queuedAt.value : this.queuedAt,
      baseUpdatedAt: data.baseUpdatedAt.present
          ? data.baseUpdatedAt.value
          : this.baseUpdatedAt,
      retryCount:
          data.retryCount.present ? data.retryCount.value : this.retryCount,
      lastAttemptAt: data.lastAttemptAt.present
          ? data.lastAttemptAt.value
          : this.lastAttemptAt,
    );
  }

  @override
  String toString() {
    return (StringBuffer('OutboxEntry(')
          ..write('id: $id, ')
          ..write('idempotencyKey: $idempotencyKey, ')
          ..write('operationType: $operationType, ')
          ..write('entityId: $entityId, ')
          ..write('payload: $payload, ')
          ..write('queuedAt: $queuedAt, ')
          ..write('baseUpdatedAt: $baseUpdatedAt, ')
          ..write('retryCount: $retryCount, ')
          ..write('lastAttemptAt: $lastAttemptAt')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(id, idempotencyKey, operationType, entityId,
      payload, queuedAt, baseUpdatedAt, retryCount, lastAttemptAt);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is OutboxEntry &&
          other.id == this.id &&
          other.idempotencyKey == this.idempotencyKey &&
          other.operationType == this.operationType &&
          other.entityId == this.entityId &&
          other.payload == this.payload &&
          other.queuedAt == this.queuedAt &&
          other.baseUpdatedAt == this.baseUpdatedAt &&
          other.retryCount == this.retryCount &&
          other.lastAttemptAt == this.lastAttemptAt);
}

class OutboxEntriesCompanion extends UpdateCompanion<OutboxEntry> {
  final Value<int> id;
  final Value<String> idempotencyKey;
  final Value<String> operationType;
  final Value<String> entityId;
  final Value<String> payload;
  final Value<DateTime> queuedAt;
  final Value<DateTime?> baseUpdatedAt;
  final Value<int> retryCount;
  final Value<DateTime?> lastAttemptAt;
  const OutboxEntriesCompanion({
    this.id = const Value.absent(),
    this.idempotencyKey = const Value.absent(),
    this.operationType = const Value.absent(),
    this.entityId = const Value.absent(),
    this.payload = const Value.absent(),
    this.queuedAt = const Value.absent(),
    this.baseUpdatedAt = const Value.absent(),
    this.retryCount = const Value.absent(),
    this.lastAttemptAt = const Value.absent(),
  });
  OutboxEntriesCompanion.insert({
    this.id = const Value.absent(),
    required String idempotencyKey,
    required String operationType,
    required String entityId,
    required String payload,
    required DateTime queuedAt,
    this.baseUpdatedAt = const Value.absent(),
    this.retryCount = const Value.absent(),
    this.lastAttemptAt = const Value.absent(),
  })  : idempotencyKey = Value(idempotencyKey),
        operationType = Value(operationType),
        entityId = Value(entityId),
        payload = Value(payload),
        queuedAt = Value(queuedAt);
  static Insertable<OutboxEntry> custom({
    Expression<int>? id,
    Expression<String>? idempotencyKey,
    Expression<String>? operationType,
    Expression<String>? entityId,
    Expression<String>? payload,
    Expression<DateTime>? queuedAt,
    Expression<DateTime>? baseUpdatedAt,
    Expression<int>? retryCount,
    Expression<DateTime>? lastAttemptAt,
  }) {
    return RawValuesInsertable({
      if (id != null) 'id': id,
      if (idempotencyKey != null) 'idempotency_key': idempotencyKey,
      if (operationType != null) 'operation_type': operationType,
      if (entityId != null) 'entity_id': entityId,
      if (payload != null) 'payload': payload,
      if (queuedAt != null) 'queued_at': queuedAt,
      if (baseUpdatedAt != null) 'base_updated_at': baseUpdatedAt,
      if (retryCount != null) 'retry_count': retryCount,
      if (lastAttemptAt != null) 'last_attempt_at': lastAttemptAt,
    });
  }

  OutboxEntriesCompanion copyWith(
      {Value<int>? id,
      Value<String>? idempotencyKey,
      Value<String>? operationType,
      Value<String>? entityId,
      Value<String>? payload,
      Value<DateTime>? queuedAt,
      Value<DateTime?>? baseUpdatedAt,
      Value<int>? retryCount,
      Value<DateTime?>? lastAttemptAt}) {
    return OutboxEntriesCompanion(
      id: id ?? this.id,
      idempotencyKey: idempotencyKey ?? this.idempotencyKey,
      operationType: operationType ?? this.operationType,
      entityId: entityId ?? this.entityId,
      payload: payload ?? this.payload,
      queuedAt: queuedAt ?? this.queuedAt,
      baseUpdatedAt: baseUpdatedAt ?? this.baseUpdatedAt,
      retryCount: retryCount ?? this.retryCount,
      lastAttemptAt: lastAttemptAt ?? this.lastAttemptAt,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (id.present) {
      map['id'] = Variable<int>(id.value);
    }
    if (idempotencyKey.present) {
      map['idempotency_key'] = Variable<String>(idempotencyKey.value);
    }
    if (operationType.present) {
      map['operation_type'] = Variable<String>(operationType.value);
    }
    if (entityId.present) {
      map['entity_id'] = Variable<String>(entityId.value);
    }
    if (payload.present) {
      map['payload'] = Variable<String>(payload.value);
    }
    if (queuedAt.present) {
      map['queued_at'] = Variable<DateTime>(queuedAt.value);
    }
    if (baseUpdatedAt.present) {
      map['base_updated_at'] = Variable<DateTime>(baseUpdatedAt.value);
    }
    if (retryCount.present) {
      map['retry_count'] = Variable<int>(retryCount.value);
    }
    if (lastAttemptAt.present) {
      map['last_attempt_at'] = Variable<DateTime>(lastAttemptAt.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('OutboxEntriesCompanion(')
          ..write('id: $id, ')
          ..write('idempotencyKey: $idempotencyKey, ')
          ..write('operationType: $operationType, ')
          ..write('entityId: $entityId, ')
          ..write('payload: $payload, ')
          ..write('queuedAt: $queuedAt, ')
          ..write('baseUpdatedAt: $baseUpdatedAt, ')
          ..write('retryCount: $retryCount, ')
          ..write('lastAttemptAt: $lastAttemptAt')
          ..write(')'))
        .toString();
  }
}

class $SyncMetaTable extends SyncMeta
    with TableInfo<$SyncMetaTable, SyncMetaData> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $SyncMetaTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _metaKeyMeta =
      const VerificationMeta('metaKey');
  @override
  late final GeneratedColumn<String> metaKey = GeneratedColumn<String>(
      'meta_key', aliasedName, false,
      type: DriftSqlType.string, requiredDuringInsert: true);
  static const VerificationMeta _metaValueMeta =
      const VerificationMeta('metaValue');
  @override
  late final GeneratedColumn<String> metaValue = GeneratedColumn<String>(
      'meta_value', aliasedName, false,
      type: DriftSqlType.string, requiredDuringInsert: true);
  @override
  List<GeneratedColumn> get $columns => [metaKey, metaValue];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'sync_meta';
  @override
  VerificationContext validateIntegrity(Insertable<SyncMetaData> instance,
      {bool isInserting = false}) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('meta_key')) {
      context.handle(_metaKeyMeta,
          metaKey.isAcceptableOrUnknown(data['meta_key']!, _metaKeyMeta));
    } else if (isInserting) {
      context.missing(_metaKeyMeta);
    }
    if (data.containsKey('meta_value')) {
      context.handle(_metaValueMeta,
          metaValue.isAcceptableOrUnknown(data['meta_value']!, _metaValueMeta));
    } else if (isInserting) {
      context.missing(_metaValueMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {metaKey};
  @override
  SyncMetaData map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return SyncMetaData(
      metaKey: attachedDatabase.typeMapping
          .read(DriftSqlType.string, data['${effectivePrefix}meta_key'])!,
      metaValue: attachedDatabase.typeMapping
          .read(DriftSqlType.string, data['${effectivePrefix}meta_value'])!,
    );
  }

  @override
  $SyncMetaTable createAlias(String alias) {
    return $SyncMetaTable(attachedDatabase, alias);
  }
}

class SyncMetaData extends DataClass implements Insertable<SyncMetaData> {
  final String metaKey;
  final String metaValue;
  const SyncMetaData({required this.metaKey, required this.metaValue});
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['meta_key'] = Variable<String>(metaKey);
    map['meta_value'] = Variable<String>(metaValue);
    return map;
  }

  SyncMetaCompanion toCompanion(bool nullToAbsent) {
    return SyncMetaCompanion(
      metaKey: Value(metaKey),
      metaValue: Value(metaValue),
    );
  }

  factory SyncMetaData.fromJson(Map<String, dynamic> json,
      {ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return SyncMetaData(
      metaKey: serializer.fromJson<String>(json['metaKey']),
      metaValue: serializer.fromJson<String>(json['metaValue']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'metaKey': serializer.toJson<String>(metaKey),
      'metaValue': serializer.toJson<String>(metaValue),
    };
  }

  SyncMetaData copyWith({String? metaKey, String? metaValue}) => SyncMetaData(
        metaKey: metaKey ?? this.metaKey,
        metaValue: metaValue ?? this.metaValue,
      );
  SyncMetaData copyWithCompanion(SyncMetaCompanion data) {
    return SyncMetaData(
      metaKey: data.metaKey.present ? data.metaKey.value : this.metaKey,
      metaValue: data.metaValue.present ? data.metaValue.value : this.metaValue,
    );
  }

  @override
  String toString() {
    return (StringBuffer('SyncMetaData(')
          ..write('metaKey: $metaKey, ')
          ..write('metaValue: $metaValue')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(metaKey, metaValue);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is SyncMetaData &&
          other.metaKey == this.metaKey &&
          other.metaValue == this.metaValue);
}

class SyncMetaCompanion extends UpdateCompanion<SyncMetaData> {
  final Value<String> metaKey;
  final Value<String> metaValue;
  final Value<int> rowid;
  const SyncMetaCompanion({
    this.metaKey = const Value.absent(),
    this.metaValue = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  SyncMetaCompanion.insert({
    required String metaKey,
    required String metaValue,
    this.rowid = const Value.absent(),
  })  : metaKey = Value(metaKey),
        metaValue = Value(metaValue);
  static Insertable<SyncMetaData> custom({
    Expression<String>? metaKey,
    Expression<String>? metaValue,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (metaKey != null) 'meta_key': metaKey,
      if (metaValue != null) 'meta_value': metaValue,
      if (rowid != null) 'rowid': rowid,
    });
  }

  SyncMetaCompanion copyWith(
      {Value<String>? metaKey, Value<String>? metaValue, Value<int>? rowid}) {
    return SyncMetaCompanion(
      metaKey: metaKey ?? this.metaKey,
      metaValue: metaValue ?? this.metaValue,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (metaKey.present) {
      map['meta_key'] = Variable<String>(metaKey.value);
    }
    if (metaValue.present) {
      map['meta_value'] = Variable<String>(metaValue.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('SyncMetaCompanion(')
          ..write('metaKey: $metaKey, ')
          ..write('metaValue: $metaValue, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

abstract class _$OfflineDatabase extends GeneratedDatabase {
  _$OfflineDatabase(QueryExecutor e) : super(e);
  $OfflineDatabaseManager get managers => $OfflineDatabaseManager(this);
  late final $CachedResponsesTable cachedResponses =
      $CachedResponsesTable(this);
  late final $OutboxEntriesTable outboxEntries = $OutboxEntriesTable(this);
  late final $SyncMetaTable syncMeta = $SyncMetaTable(this);
  @override
  Iterable<TableInfo<Table, Object?>> get allTables =>
      allSchemaEntities.whereType<TableInfo<Table, Object?>>();
  @override
  List<DatabaseSchemaEntity> get allSchemaEntities =>
      [cachedResponses, outboxEntries, syncMeta];
}

typedef $$CachedResponsesTableCreateCompanionBuilder = CachedResponsesCompanion
    Function({
  required String cacheKey,
  required String endpoint,
  required String payload,
  required DateTime fetchedAt,
  Value<int> rowid,
});
typedef $$CachedResponsesTableUpdateCompanionBuilder = CachedResponsesCompanion
    Function({
  Value<String> cacheKey,
  Value<String> endpoint,
  Value<String> payload,
  Value<DateTime> fetchedAt,
  Value<int> rowid,
});

class $$CachedResponsesTableFilterComposer
    extends Composer<_$OfflineDatabase, $CachedResponsesTable> {
  $$CachedResponsesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get cacheKey => $composableBuilder(
      column: $table.cacheKey, builder: (column) => ColumnFilters(column));

  ColumnFilters<String> get endpoint => $composableBuilder(
      column: $table.endpoint, builder: (column) => ColumnFilters(column));

  ColumnFilters<String> get payload => $composableBuilder(
      column: $table.payload, builder: (column) => ColumnFilters(column));

  ColumnFilters<DateTime> get fetchedAt => $composableBuilder(
      column: $table.fetchedAt, builder: (column) => ColumnFilters(column));
}

class $$CachedResponsesTableOrderingComposer
    extends Composer<_$OfflineDatabase, $CachedResponsesTable> {
  $$CachedResponsesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get cacheKey => $composableBuilder(
      column: $table.cacheKey, builder: (column) => ColumnOrderings(column));

  ColumnOrderings<String> get endpoint => $composableBuilder(
      column: $table.endpoint, builder: (column) => ColumnOrderings(column));

  ColumnOrderings<String> get payload => $composableBuilder(
      column: $table.payload, builder: (column) => ColumnOrderings(column));

  ColumnOrderings<DateTime> get fetchedAt => $composableBuilder(
      column: $table.fetchedAt, builder: (column) => ColumnOrderings(column));
}

class $$CachedResponsesTableAnnotationComposer
    extends Composer<_$OfflineDatabase, $CachedResponsesTable> {
  $$CachedResponsesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get cacheKey =>
      $composableBuilder(column: $table.cacheKey, builder: (column) => column);

  GeneratedColumn<String> get endpoint =>
      $composableBuilder(column: $table.endpoint, builder: (column) => column);

  GeneratedColumn<String> get payload =>
      $composableBuilder(column: $table.payload, builder: (column) => column);

  GeneratedColumn<DateTime> get fetchedAt =>
      $composableBuilder(column: $table.fetchedAt, builder: (column) => column);
}

class $$CachedResponsesTableTableManager extends RootTableManager<
    _$OfflineDatabase,
    $CachedResponsesTable,
    CachedResponse,
    $$CachedResponsesTableFilterComposer,
    $$CachedResponsesTableOrderingComposer,
    $$CachedResponsesTableAnnotationComposer,
    $$CachedResponsesTableCreateCompanionBuilder,
    $$CachedResponsesTableUpdateCompanionBuilder,
    (
      CachedResponse,
      BaseReferences<_$OfflineDatabase, $CachedResponsesTable, CachedResponse>
    ),
    CachedResponse,
    PrefetchHooks Function()> {
  $$CachedResponsesTableTableManager(
      _$OfflineDatabase db, $CachedResponsesTable table)
      : super(TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$CachedResponsesTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$CachedResponsesTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$CachedResponsesTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback: ({
            Value<String> cacheKey = const Value.absent(),
            Value<String> endpoint = const Value.absent(),
            Value<String> payload = const Value.absent(),
            Value<DateTime> fetchedAt = const Value.absent(),
            Value<int> rowid = const Value.absent(),
          }) =>
              CachedResponsesCompanion(
            cacheKey: cacheKey,
            endpoint: endpoint,
            payload: payload,
            fetchedAt: fetchedAt,
            rowid: rowid,
          ),
          createCompanionCallback: ({
            required String cacheKey,
            required String endpoint,
            required String payload,
            required DateTime fetchedAt,
            Value<int> rowid = const Value.absent(),
          }) =>
              CachedResponsesCompanion.insert(
            cacheKey: cacheKey,
            endpoint: endpoint,
            payload: payload,
            fetchedAt: fetchedAt,
            rowid: rowid,
          ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ));
}

typedef $$CachedResponsesTableProcessedTableManager = ProcessedTableManager<
    _$OfflineDatabase,
    $CachedResponsesTable,
    CachedResponse,
    $$CachedResponsesTableFilterComposer,
    $$CachedResponsesTableOrderingComposer,
    $$CachedResponsesTableAnnotationComposer,
    $$CachedResponsesTableCreateCompanionBuilder,
    $$CachedResponsesTableUpdateCompanionBuilder,
    (
      CachedResponse,
      BaseReferences<_$OfflineDatabase, $CachedResponsesTable, CachedResponse>
    ),
    CachedResponse,
    PrefetchHooks Function()>;
typedef $$OutboxEntriesTableCreateCompanionBuilder = OutboxEntriesCompanion
    Function({
  Value<int> id,
  required String idempotencyKey,
  required String operationType,
  required String entityId,
  required String payload,
  required DateTime queuedAt,
  Value<DateTime?> baseUpdatedAt,
  Value<int> retryCount,
  Value<DateTime?> lastAttemptAt,
});
typedef $$OutboxEntriesTableUpdateCompanionBuilder = OutboxEntriesCompanion
    Function({
  Value<int> id,
  Value<String> idempotencyKey,
  Value<String> operationType,
  Value<String> entityId,
  Value<String> payload,
  Value<DateTime> queuedAt,
  Value<DateTime?> baseUpdatedAt,
  Value<int> retryCount,
  Value<DateTime?> lastAttemptAt,
});

class $$OutboxEntriesTableFilterComposer
    extends Composer<_$OfflineDatabase, $OutboxEntriesTable> {
  $$OutboxEntriesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<int> get id => $composableBuilder(
      column: $table.id, builder: (column) => ColumnFilters(column));

  ColumnFilters<String> get idempotencyKey => $composableBuilder(
      column: $table.idempotencyKey,
      builder: (column) => ColumnFilters(column));

  ColumnFilters<String> get operationType => $composableBuilder(
      column: $table.operationType, builder: (column) => ColumnFilters(column));

  ColumnFilters<String> get entityId => $composableBuilder(
      column: $table.entityId, builder: (column) => ColumnFilters(column));

  ColumnFilters<String> get payload => $composableBuilder(
      column: $table.payload, builder: (column) => ColumnFilters(column));

  ColumnFilters<DateTime> get queuedAt => $composableBuilder(
      column: $table.queuedAt, builder: (column) => ColumnFilters(column));

  ColumnFilters<DateTime> get baseUpdatedAt => $composableBuilder(
      column: $table.baseUpdatedAt, builder: (column) => ColumnFilters(column));

  ColumnFilters<int> get retryCount => $composableBuilder(
      column: $table.retryCount, builder: (column) => ColumnFilters(column));

  ColumnFilters<DateTime> get lastAttemptAt => $composableBuilder(
      column: $table.lastAttemptAt, builder: (column) => ColumnFilters(column));
}

class $$OutboxEntriesTableOrderingComposer
    extends Composer<_$OfflineDatabase, $OutboxEntriesTable> {
  $$OutboxEntriesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<int> get id => $composableBuilder(
      column: $table.id, builder: (column) => ColumnOrderings(column));

  ColumnOrderings<String> get idempotencyKey => $composableBuilder(
      column: $table.idempotencyKey,
      builder: (column) => ColumnOrderings(column));

  ColumnOrderings<String> get operationType => $composableBuilder(
      column: $table.operationType,
      builder: (column) => ColumnOrderings(column));

  ColumnOrderings<String> get entityId => $composableBuilder(
      column: $table.entityId, builder: (column) => ColumnOrderings(column));

  ColumnOrderings<String> get payload => $composableBuilder(
      column: $table.payload, builder: (column) => ColumnOrderings(column));

  ColumnOrderings<DateTime> get queuedAt => $composableBuilder(
      column: $table.queuedAt, builder: (column) => ColumnOrderings(column));

  ColumnOrderings<DateTime> get baseUpdatedAt => $composableBuilder(
      column: $table.baseUpdatedAt,
      builder: (column) => ColumnOrderings(column));

  ColumnOrderings<int> get retryCount => $composableBuilder(
      column: $table.retryCount, builder: (column) => ColumnOrderings(column));

  ColumnOrderings<DateTime> get lastAttemptAt => $composableBuilder(
      column: $table.lastAttemptAt,
      builder: (column) => ColumnOrderings(column));
}

class $$OutboxEntriesTableAnnotationComposer
    extends Composer<_$OfflineDatabase, $OutboxEntriesTable> {
  $$OutboxEntriesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<int> get id =>
      $composableBuilder(column: $table.id, builder: (column) => column);

  GeneratedColumn<String> get idempotencyKey => $composableBuilder(
      column: $table.idempotencyKey, builder: (column) => column);

  GeneratedColumn<String> get operationType => $composableBuilder(
      column: $table.operationType, builder: (column) => column);

  GeneratedColumn<String> get entityId =>
      $composableBuilder(column: $table.entityId, builder: (column) => column);

  GeneratedColumn<String> get payload =>
      $composableBuilder(column: $table.payload, builder: (column) => column);

  GeneratedColumn<DateTime> get queuedAt =>
      $composableBuilder(column: $table.queuedAt, builder: (column) => column);

  GeneratedColumn<DateTime> get baseUpdatedAt => $composableBuilder(
      column: $table.baseUpdatedAt, builder: (column) => column);

  GeneratedColumn<int> get retryCount => $composableBuilder(
      column: $table.retryCount, builder: (column) => column);

  GeneratedColumn<DateTime> get lastAttemptAt => $composableBuilder(
      column: $table.lastAttemptAt, builder: (column) => column);
}

class $$OutboxEntriesTableTableManager extends RootTableManager<
    _$OfflineDatabase,
    $OutboxEntriesTable,
    OutboxEntry,
    $$OutboxEntriesTableFilterComposer,
    $$OutboxEntriesTableOrderingComposer,
    $$OutboxEntriesTableAnnotationComposer,
    $$OutboxEntriesTableCreateCompanionBuilder,
    $$OutboxEntriesTableUpdateCompanionBuilder,
    (
      OutboxEntry,
      BaseReferences<_$OfflineDatabase, $OutboxEntriesTable, OutboxEntry>
    ),
    OutboxEntry,
    PrefetchHooks Function()> {
  $$OutboxEntriesTableTableManager(
      _$OfflineDatabase db, $OutboxEntriesTable table)
      : super(TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$OutboxEntriesTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$OutboxEntriesTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$OutboxEntriesTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback: ({
            Value<int> id = const Value.absent(),
            Value<String> idempotencyKey = const Value.absent(),
            Value<String> operationType = const Value.absent(),
            Value<String> entityId = const Value.absent(),
            Value<String> payload = const Value.absent(),
            Value<DateTime> queuedAt = const Value.absent(),
            Value<DateTime?> baseUpdatedAt = const Value.absent(),
            Value<int> retryCount = const Value.absent(),
            Value<DateTime?> lastAttemptAt = const Value.absent(),
          }) =>
              OutboxEntriesCompanion(
            id: id,
            idempotencyKey: idempotencyKey,
            operationType: operationType,
            entityId: entityId,
            payload: payload,
            queuedAt: queuedAt,
            baseUpdatedAt: baseUpdatedAt,
            retryCount: retryCount,
            lastAttemptAt: lastAttemptAt,
          ),
          createCompanionCallback: ({
            Value<int> id = const Value.absent(),
            required String idempotencyKey,
            required String operationType,
            required String entityId,
            required String payload,
            required DateTime queuedAt,
            Value<DateTime?> baseUpdatedAt = const Value.absent(),
            Value<int> retryCount = const Value.absent(),
            Value<DateTime?> lastAttemptAt = const Value.absent(),
          }) =>
              OutboxEntriesCompanion.insert(
            id: id,
            idempotencyKey: idempotencyKey,
            operationType: operationType,
            entityId: entityId,
            payload: payload,
            queuedAt: queuedAt,
            baseUpdatedAt: baseUpdatedAt,
            retryCount: retryCount,
            lastAttemptAt: lastAttemptAt,
          ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ));
}

typedef $$OutboxEntriesTableProcessedTableManager = ProcessedTableManager<
    _$OfflineDatabase,
    $OutboxEntriesTable,
    OutboxEntry,
    $$OutboxEntriesTableFilterComposer,
    $$OutboxEntriesTableOrderingComposer,
    $$OutboxEntriesTableAnnotationComposer,
    $$OutboxEntriesTableCreateCompanionBuilder,
    $$OutboxEntriesTableUpdateCompanionBuilder,
    (
      OutboxEntry,
      BaseReferences<_$OfflineDatabase, $OutboxEntriesTable, OutboxEntry>
    ),
    OutboxEntry,
    PrefetchHooks Function()>;
typedef $$SyncMetaTableCreateCompanionBuilder = SyncMetaCompanion Function({
  required String metaKey,
  required String metaValue,
  Value<int> rowid,
});
typedef $$SyncMetaTableUpdateCompanionBuilder = SyncMetaCompanion Function({
  Value<String> metaKey,
  Value<String> metaValue,
  Value<int> rowid,
});

class $$SyncMetaTableFilterComposer
    extends Composer<_$OfflineDatabase, $SyncMetaTable> {
  $$SyncMetaTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get metaKey => $composableBuilder(
      column: $table.metaKey, builder: (column) => ColumnFilters(column));

  ColumnFilters<String> get metaValue => $composableBuilder(
      column: $table.metaValue, builder: (column) => ColumnFilters(column));
}

class $$SyncMetaTableOrderingComposer
    extends Composer<_$OfflineDatabase, $SyncMetaTable> {
  $$SyncMetaTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get metaKey => $composableBuilder(
      column: $table.metaKey, builder: (column) => ColumnOrderings(column));

  ColumnOrderings<String> get metaValue => $composableBuilder(
      column: $table.metaValue, builder: (column) => ColumnOrderings(column));
}

class $$SyncMetaTableAnnotationComposer
    extends Composer<_$OfflineDatabase, $SyncMetaTable> {
  $$SyncMetaTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get metaKey =>
      $composableBuilder(column: $table.metaKey, builder: (column) => column);

  GeneratedColumn<String> get metaValue =>
      $composableBuilder(column: $table.metaValue, builder: (column) => column);
}

class $$SyncMetaTableTableManager extends RootTableManager<
    _$OfflineDatabase,
    $SyncMetaTable,
    SyncMetaData,
    $$SyncMetaTableFilterComposer,
    $$SyncMetaTableOrderingComposer,
    $$SyncMetaTableAnnotationComposer,
    $$SyncMetaTableCreateCompanionBuilder,
    $$SyncMetaTableUpdateCompanionBuilder,
    (
      SyncMetaData,
      BaseReferences<_$OfflineDatabase, $SyncMetaTable, SyncMetaData>
    ),
    SyncMetaData,
    PrefetchHooks Function()> {
  $$SyncMetaTableTableManager(_$OfflineDatabase db, $SyncMetaTable table)
      : super(TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$SyncMetaTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$SyncMetaTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$SyncMetaTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback: ({
            Value<String> metaKey = const Value.absent(),
            Value<String> metaValue = const Value.absent(),
            Value<int> rowid = const Value.absent(),
          }) =>
              SyncMetaCompanion(
            metaKey: metaKey,
            metaValue: metaValue,
            rowid: rowid,
          ),
          createCompanionCallback: ({
            required String metaKey,
            required String metaValue,
            Value<int> rowid = const Value.absent(),
          }) =>
              SyncMetaCompanion.insert(
            metaKey: metaKey,
            metaValue: metaValue,
            rowid: rowid,
          ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ));
}

typedef $$SyncMetaTableProcessedTableManager = ProcessedTableManager<
    _$OfflineDatabase,
    $SyncMetaTable,
    SyncMetaData,
    $$SyncMetaTableFilterComposer,
    $$SyncMetaTableOrderingComposer,
    $$SyncMetaTableAnnotationComposer,
    $$SyncMetaTableCreateCompanionBuilder,
    $$SyncMetaTableUpdateCompanionBuilder,
    (
      SyncMetaData,
      BaseReferences<_$OfflineDatabase, $SyncMetaTable, SyncMetaData>
    ),
    SyncMetaData,
    PrefetchHooks Function()>;

class $OfflineDatabaseManager {
  final _$OfflineDatabase _db;
  $OfflineDatabaseManager(this._db);
  $$CachedResponsesTableTableManager get cachedResponses =>
      $$CachedResponsesTableTableManager(_db, _db.cachedResponses);
  $$OutboxEntriesTableTableManager get outboxEntries =>
      $$OutboxEntriesTableTableManager(_db, _db.outboxEntries);
  $$SyncMetaTableTableManager get syncMeta =>
      $$SyncMetaTableTableManager(_db, _db.syncMeta);
}
