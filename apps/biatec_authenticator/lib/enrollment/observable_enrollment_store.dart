import 'package:flutter/foundation.dart';

import 'enrollment_state.dart';
import 'enrollment_store.dart';

/// An [EnrollmentStore] that tells listeners when the stored enrollment changed, so the app notices at once when this device signs itself out (cancelling its own enrollment in Devices) instead of at the next tab change.
class ObservableEnrollmentStore extends ChangeNotifier
    implements EnrollmentStore {
  ObservableEnrollmentStore(this._inner);

  final EnrollmentStore _inner;

  /// How many times the enrollment was deliberately cleared. A record that is merely missing (an enrollment kept in memory after a failed write) is not a clear.
  int get clearCount => _clearCount;
  int _clearCount = 0;

  /// An enrollment whose write failed. The server has it, so the whole app (the pending list, settings, push) works from this copy until a later read manages to save it.
  EnrollmentRecord? _unsaved;

  @override
  Future<EnrollmentRecord?> read() async {
    final stored = await _inner.read();
    final unsaved = _unsaved;
    if (stored != null || unsaved == null) return stored;
    try {
      await _inner.write(unsaved);
      _unsaved = null;
    } on Object {
      // Still in memory only; tried again at the next read.
    }
    return unsaved;
  }

  @override
  Future<void> write(EnrollmentRecord record) async {
    try {
      await _inner.write(record);
      _unsaved = null;
    } on Object {
      _unsaved = record;
      rethrow;
    }
    notifyListeners();
  }

  /// Announced even when clearing the storage fails: the device was signed out on purpose, and the app must stop using it now.
  @override
  Future<void> clear() async {
    _unsaved = null;
    try {
      await _inner.clear();
    } finally {
      _clearCount++;
      notifyListeners();
    }
  }

  @override
  Future<String?> readAlias() => _inner.readAlias();

  @override
  Future<void> writeAlias(String alias) => _inner.writeAlias(alias);

  @override
  Future<void> clearAlias() => _inner.clearAlias();
}
