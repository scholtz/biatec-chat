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

  @override
  Future<EnrollmentRecord?> read() => _inner.read();

  @override
  Future<void> write(EnrollmentRecord record) async {
    await _inner.write(record);
    notifyListeners();
  }

  @override
  Future<void> clear() async {
    await _inner.clear();
    _clearCount++;
    notifyListeners();
  }

  @override
  Future<String?> readAlias() => _inner.readAlias();

  @override
  Future<void> writeAlias(String alias) => _inner.writeAlias(alias);

  @override
  Future<void> clearAlias() => _inner.clearAlias();
}
