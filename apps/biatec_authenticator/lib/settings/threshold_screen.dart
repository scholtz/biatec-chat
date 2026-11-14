import 'package:flutter/material.dart';

import '../clients/amounts.dart';
import '../clients/trust_strings.dart';
import '../clients/trust_widgets.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';
import '../theme.dart';

/// Settings, Security, Approval threshold: the amount above which a request asks on this phone, the user's own choice with no ceiling of the app's (an operator may set one; its refusal is shown as
/// it is said). Also "Ask on test networks" and "Block services without a proven domain".
///
/// The amount is a decimal string from the field to the wire. Lowering applies at once; raising is signed by the approval key (one biometric prompt), which the controller does when Biatec OIDC asks for it.
class ThresholdScreen extends StatelessWidget {
  const ThresholdScreen({super.key, required this.services, this.controller});

  final SettingsServices services;

  /// Shared with the other settings screens (see [SettingsFrame.controller]).
  final SettingsController? controller;

  @override
  Widget build(BuildContext context) {
    final s = TrustStrings.of();
    return SettingsFrame(
      title: s.t('thresholdTitle'),
      services: services,
      controller: controller,
      builder: (context, c) => [
        ...inactiveBanner(c),
        _ThresholdForm(key: const Key('threshold-form'), controller: c),
      ],
    );
  }
}

/// The ready-made choices, as decimal strings. 0 is "Always ask".
const thresholdChoices = ['0', '100', '500', '1000', '5000'];

class _ThresholdForm extends StatefulWidget {
  const _ThresholdForm({super.key, required this.controller});

  final SettingsController controller;

  @override
  State<_ThresholdForm> createState() => _ThresholdFormState();
}

class _ThresholdFormState extends State<_ThresholdForm> {
  late final TextEditingController _text;
  final _focus = FocusNode();
  String? _problem;

  @override
  void initState() {
    super.initState();
    _text = TextEditingController(
        text: widget.controller.settings!.thresholdEur);
  }

  @override
  void dispose() {
    _text.dispose();
    _focus.dispose();
    super.dispose();
  }

  /// The amount typed, or null when it is not one.
  String? get _typed {
    final v = _text.text.trim();
    return amountFormat.hasMatch(v) ? v : null;
  }

  Future<void> _save(SettingsController c) async {
    final v = _typed;
    if (v == null) {
      setState(() => _problem = TrustStrings.of().t('amountProblem'));
      return;
    }
    final ok = await c.apply({'threshold_eur': v});
    if (ok && mounted && c.settings != null) {
      // The server's own spelling of the amount (`250.00`).
      setState(() => _text.text = c.settings!.thresholdEur);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.controller;
    final s = TrustStrings.of();
    final theme = Theme.of(context);
    final settings = c.settings!;
    final enabled = !c.busy && c.deviceActive;
    final typed = _typed;
    final cmp = typed == null ? null : compareAmounts(typed, settings.thresholdEur);
    final String? effect = cmp == null
        ? null
        : cmp > 0
            ? s.t('raiseExplain')
            : cmp < 0
                ? s.t('lowerExplain')
                : s.t('sameAmount');
    bool chosen(String choice) =>
        typed != null && compareAmounts(typed, choice) == 0;
    final isChoice = thresholdChoices.any(chosen);
    return Padding(
      padding: const EdgeInsets.all(AppSpacing.m),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Semantics(
          liveRegion: true,
          child: Text(
              '${s.t('currentThreshold')}: ${thresholdText(settings.thresholdEur)}',
              key: const Key('threshold-current'),
              style: theme.textTheme.titleLarge
                  ?.copyWith(fontWeight: FontWeight.w700)),
        ),
        const SizedBox(height: AppSpacing.xs),
        Text(s.t('thresholdIntro'), style: theme.textTheme.bodyMedium),
        const SizedBox(height: AppSpacing.m),
        Wrap(spacing: AppSpacing.s, runSpacing: AppSpacing.s, children: [
          for (final choice in thresholdChoices)
            ChoiceChip(
              key: Key('threshold-choice-$choice'),
              label: Text(choice == '0' ? s.t('alwaysAsk') : '$choice ${s.t('eur')}'),
              selected: chosen(choice),
              onSelected: enabled
                  ? (_) => setState(() {
                        _text.text = choice;
                        _problem = null;
                      })
                  : null,
            ),
          ChoiceChip(
            key: const Key('threshold-choice-other'),
            label: Text(s.t('otherAmount')),
            selected: !isChoice,
            onSelected: enabled ? (_) => _focus.requestFocus() : null,
          ),
        ]),
        const SizedBox(height: AppSpacing.m),
        TextField(
          key: const Key('threshold-field'),
          controller: _text,
          focusNode: _focus,
          enabled: enabled,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          onChanged: (_) => setState(() => _problem = null),
          onSubmitted: (_) => _save(c),
          decoration: InputDecoration(
            labelText: s.t('amount'),
            suffixText: s.t('eur'),
            helperText: effect ?? s.t('thresholdHelp'),
            helperMaxLines: 4,
            errorText: _problem,
          ),
        ),
        if (cmp != null && cmp > 0) ...[
          const SizedBox(height: AppSpacing.xs),
          Text(s.t('raiseNeedsApproval'),
              key: const Key('threshold-raise-note'),
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
        ],
        const SizedBox(height: AppSpacing.m),
        FilledButton(
          key: const Key('threshold-save'),
          onPressed: enabled ? () => _save(c) : null,
          child: Text(s.t('save')),
        ),
        const Divider(height: AppSpacing.xl),
        SwitchListTile(
          key: const Key('ask-test-networks'),
          contentPadding: EdgeInsets.zero,
          title: Text(s.t('askTestNetworks')),
          subtitle: Text(s.t('askTestNetworksHelp')),
          value: settings.askOnTestNetworks,
          onChanged: enabled
              ? (v) => c.apply({'ask_on_test_networks': v})
              : null,
        ),
        SwitchListTile(
          key: const Key('block-unproven'),
          contentPadding: EdgeInsets.zero,
          title: Text(s.t('blockUnproven')),
          subtitle: Text(s.t('blockUnprovenHelp')),
          value: settings.blockUnprovenClients,
          onChanged: enabled
              ? (v) => c.apply({'block_unproven_clients': v})
              : null,
        ),
      ]),
    );
  }
}
