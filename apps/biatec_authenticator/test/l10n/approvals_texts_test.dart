import 'dart:ui';

import 'package:biatec_authenticator/approvals/approval_texts.dart';
import 'package:biatec_authenticator/approvals/history_widgets/day_groups.dart';
import 'package:biatec_authenticator/approvals/history_widgets/history_filters.dart';
import 'package:biatec_authenticator/approvals/history_widgets/history_status.dart';
import 'package:biatec_authenticator/approvals/history_widgets/history_texts.dart';
import 'package:biatec_authenticator/approvals/widgets/expiry.dart';
import 'package:biatec_authenticator/text/rp_strings.dart';
import 'package:flutter_test/flutter_test.dart';

import 'texts_support.dart';

const _sk = Locale('sk');

void main() {
  test('the approval texts have the same keys and places in both languages',
      () {
    expectSameTexts(
        'ApprovalTexts', ApprovalTexts.en.values, ApprovalTexts.sk.values);
  });

  test('the history texts have the same keys and places in both languages',
      () {
    expectSameTexts(
        'HistoryTexts', HistoryTexts.en.values, HistoryTexts.sk.values);
  });

  test('the relying-party texts have the same keys and places in both languages',
      () {
    expectSameTexts('RpStrings', RpStrings.en.values, RpStrings.sk.values);
  });

  test('each group is Slovak for sk and English otherwise', () {
    expect(ApprovalTexts.of(_sk).text('approve'), 'Schváliť');
    expect(ApprovalTexts.of(const Locale('de')).text('approve'), 'Approve');
    expect(HistoryTexts.of(_sk).text('today'), 'Dnes');
    expect(RpStrings.of(_sk).t('linkButton'), 'Prepojiť');
    expect(RpStrings.of(const Locale('en')).t('linkButton'), 'Link');
  });

  test('a message of the controller is recognised in either language', () {
    for (final t in [ApprovalTexts.en, ApprovalTexts.sk]) {
      expect(
          ApprovalTexts.isMessage(
              t.text('wrongCodeN', {'n': 2}), 'wrongCodeN'),
          isTrue);
      expect(
          ApprovalTexts.isMessage(
              t.text('couldNotApprove', {'why': 'x'}), 'couldNotApprove'),
          isTrue);
      expect(ApprovalTexts.isMessage(t.text('requestChanged'), 'requestChanged'),
          isTrue);
    }
    expect(ApprovalTexts.isMessage('Something else', 'requestChanged'), isFalse);
  });

  test('the time left is said in Slovak with the right plural', () {
    expect(spokenLeft(const Duration(minutes: 1, seconds: 5), _sk),
        'vyprší o 1 minútu');
    expect(spokenLeft(const Duration(minutes: 3), _sk), 'vyprší o 3 minúty');
    expect(spokenLeft(const Duration(minutes: 7), _sk), 'vyprší o 7 minút');
    expect(coarseLeft(const Duration(minutes: 4), _sk), 'Zostáva 4 min');
    expect(spokenLeft(const Duration(minutes: 3)), 'expires in 3 minutes');
  });

  test('the history days, statuses and filters are Slovak for sk', () {
    final now = DateTime(2026, 10, 24, 15);
    const format = DayFormat(locale: _sk);
    expect(dayLabel(DateTime(2026, 10, 24, 9), now, format), 'Dnes');
    expect(dayLabel(DateTime(2026, 10, 23, 9), now, format), 'Včera');
    // Without the Material texts a Slovak date is written in numbers, never with English names.
    expect(dayLabel(DateTime(2026, 10, 20, 9), now, format), '20. 10. 2026');
    expect(statusLabel('code_failed', _sk),
        'Odmietnuté po troch nesprávnych kódoch');
    expect(HistoryStatus.denied.labelIn(_sk), 'Zamietnuté');
    expect(HistoryFilter.all.labelIn(_sk), 'Všetko');
    expect(HistoryFilter.denied.emptyTextIn(_sk), 'Zatiaľ nič nezamietnuté');
  });
}
