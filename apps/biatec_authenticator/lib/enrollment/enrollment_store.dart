import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../security/secure_storage.dart';
import 'enrollment_state.dart';

abstract class EnrollmentStore {
  Future<EnrollmentRecord?> read();
  Future<void> write(EnrollmentRecord record);
  Future<void> clear();

  /// The keystore alias (also the device kid) of this install, generated on
  /// first enrollment so it is unique per device.
  Future<String?> readAlias();
  Future<void> writeAlias(String alias);
  Future<void> clearAlias();
}

class SecureEnrollmentStore implements EnrollmentStore {
  SecureEnrollmentStore([FlutterSecureStorage? storage])
      : _storage = storage ?? deviceBoundStorage;

  static const _key = enrollmentStorageKey;
  final FlutterSecureStorage _storage;

  @override
  Future<EnrollmentRecord?> read() async {
    final raw = await _storage.read(key: _key);
    if (raw == null) return null;
    try {
      return EnrollmentRecord.fromJson(
          (jsonDecode(raw) as Map).cast<String, Object?>());
    } on Object {
      // A corrupt record counts as not enrolled.
      return null;
    }
  }

  static const _aliasKey = enrollmentAliasStorageKey;

  @override
  Future<String?> readAlias() => _storage.read(key: _aliasKey);

  @override
  Future<void> writeAlias(String alias) =>
      _storage.write(key: _aliasKey, value: alias);

  @override
  Future<void> clearAlias() => _storage.delete(key: _aliasKey);

  @override
  Future<void> write(EnrollmentRecord record) =>
      _storage.write(key: _key, value: jsonEncode(record.toJson()));

  @override
  Future<void> clear() => _storage.delete(key: _key);
}

class MemoryEnrollmentStore implements EnrollmentStore {
  EnrollmentRecord? _record;
  String? _alias;

  @override
  Future<String?> readAlias() async => _alias;

  @override
  Future<void> writeAlias(String alias) async => _alias = alias;

  @override
  Future<void> clearAlias() async => _alias = null;

  @override
  Future<EnrollmentRecord?> read() async => _record;

  @override
  Future<void> write(EnrollmentRecord record) async => _record = record;

  @override
  Future<void> clear() async => _record = null;
}
