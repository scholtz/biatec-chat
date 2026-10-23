import 'dart:convert';

import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:flutter_test/flutter_test.dart';

HistoryEntry entry({Map<String, Object?>? signed}) => HistoryEntry(
      authReqId: 'ciba_1',
      decision: 'approve',
      summary: 'Send 5000 USDC',
      clientLabel: 'Biatec Chat',
      decidedAt: DateTime.utc(2026, 10, 23),
      receipt: {
        'version': '1',
        'alg': 'ES256',
        'signature': 'sig',
        'signed': signed ??
            {'auth_req_id': 'ciba_1', 'binding_code': '483920', 'decision': 'approve'},
      },
    );

void main() {
  group('history redaction (BCHAT-031)', () {
    test('the binding code is not kept; the rest of the receipt is', () async {
      final store = MemoryHistoryStore();
      await store.add(entry());
      final kept = (await store.read()).single.receipt;
      expect(jsonEncode(kept), isNot(contains('483920')));
      expect((kept['signed'] as Map)['auth_req_id'], 'ciba_1');
      expect(kept['signature'], 'sig');
      expect(kept['redacted'], ['signed.binding_code']);
    });

    test('an entry without a code is left as it is', () {
      final e = entry(signed: {'auth_req_id': 'ciba_2'});
      expect(redactReceipt(e.receipt), same(e.receipt));
    });

    test('older persisted entries lose the code when read', () {
      final stored = jsonDecode(jsonEncode(entry().toJson())) as Map<String, Object?>;
      final read = HistoryEntry.fromJson(stored);
      expect(jsonEncode(read.receipt), isNot(contains('483920')));
    });
  });
}
