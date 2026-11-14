import 'package:flutter/material.dart';

import '../security/settings_controller.dart';
import '../security/settings_frame.dart' show whenText;
import '../theme.dart';
import '../ui/ui.dart';
import 'amounts.dart';
import 'trust_strings.dart';

/// Why everything is read-only on an inactive device (rule 7: the first 24 hours), or nothing when the device is active. Shown above the settings of every trust screen.
List<Widget> inactiveBanner(SettingsController c) {
  if (c.deviceActive) return const [];
  final s = TrustStrings.of();
  final at = c.activeAt;
  return [
    Padding(
      key: const Key('device-inactive'),
      padding: const EdgeInsets.all(AppSpacing.s),
      child: StatusBanner(
        severity: StatusSeverity.warning,
        icon: Icons.lock_clock,
        title: s.t('inactiveTitle'),
        message: at == null
            ? s.t('inactiveBody')
            : '${s.t('inactiveBody')} ${s.t('inactiveUntil', {'when': whenText(at)})}',
      ),
    ),
  ];
}

/// The amount the user sees for a threshold: "Always ask" for 0, else the amount and EUR.
String thresholdText(String thresholdEur) {
  final s = TrustStrings.of();
  return isZeroAmount(thresholdEur)
      ? s.t('alwaysAsk')
      : '$thresholdEur ${s.t('eur')}';
}

/// Asks for a EUR amount in a sheet from the bottom of the screen: the field, a sentence that says what the amount does compared with [current] as it is typed, and Save. Null when cancelled.
Future<String?> askAmount(BuildContext context,
    {required String title,
    required String initial,
    required String? current,
    String? hint}) {
  return showModalBottomSheet<String>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (_) => _AmountSheet(
        title: title, initial: initial, current: current, hint: hint),
  );
}

class _AmountSheet extends StatefulWidget {
  const _AmountSheet(
      {required this.title,
      required this.initial,
      required this.current,
      this.hint});

  final String title;
  final String initial;
  final String? current;
  final String? hint;

  @override
  State<_AmountSheet> createState() => _AmountSheetState();
}

class _AmountSheetState extends State<_AmountSheet> {
  late final TextEditingController _text =
      TextEditingController(text: widget.initial);
  String? _problem;

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  void _submit() {
    final v = _text.text.trim();
    if (!amountFormat.hasMatch(v)) {
      setState(() => _problem = TrustStrings.of().t('amountProblem'));
      return;
    }
    Navigator.of(context).pop(v);
  }

  @override
  Widget build(BuildContext context) {
    final s = TrustStrings.of();
    final theme = Theme.of(context);
    final typed = _text.text.trim();
    final cmp = amountFormat.hasMatch(typed)
        ? compareAmounts(typed, widget.current)
        : null;
    final effect = cmp == null
        ? widget.hint
        : cmp > 0
            ? s.t('raiseExplain')
            : cmp < 0
                ? s.t('lowerExplain')
                : s.t('sameAmount');
    return SafeArea(
      child: SingleChildScrollView(
        padding: EdgeInsets.fromLTRB(AppSpacing.l, 0, AppSpacing.l,
            AppSpacing.l + MediaQuery.viewInsetsOf(context).bottom),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Semantics(
                header: true,
                child: Text(widget.title, style: theme.textTheme.titleLarge)),
            const SizedBox(height: AppSpacing.m),
            TextField(
              key: const Key('amount-field'),
              controller: _text,
              autofocus: true,
              keyboardType:
                  const TextInputType.numberWithOptions(decimal: true),
              onChanged: (_) => setState(() => _problem = null),
              onSubmitted: (_) => _submit(),
              decoration: InputDecoration(
                labelText: s.t('amount'),
                suffixText: s.t('eur'),
                helperText: effect,
                helperMaxLines: 3,
                errorText: _problem,
              ),
            ),
            const SizedBox(height: AppSpacing.l),
            FilledButton(
                key: const Key('amount-save'),
                onPressed: _submit,
                child: Text(s.t('save'))),
            const SizedBox(height: AppSpacing.s),
            OutlinedButton(
                key: const Key('amount-cancel'),
                onPressed: () => Navigator.of(context).pop(),
                child: Text(s.t('cancel'))),
          ],
        ),
      ),
    );
  }
}
