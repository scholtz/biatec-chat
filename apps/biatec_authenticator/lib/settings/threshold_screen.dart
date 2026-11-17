import 'package:material_ui/material_ui.dart';

import '../clients/amounts.dart';
import '../clients/trust_strings.dart';
import '../clients/trust_widgets.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';
import '../theme.dart';
import '../ui/ui.dart';

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
    final s = TrustStrings.of(Localizations.maybeLocaleOf(context));
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

  /// The threshold the field was last in step with: what Biatec OIDC held when the field was filled in.
  late String _shown;

  /// Set when the threshold changed underneath an amount the user had typed: the field was reset, and says so.
  bool _changedUnderneath = false;

  @override
  void initState() {
    super.initState();
    _shown = widget.controller.settings!.thresholdEur;
    _text = TextEditingController(text: _shown);
    widget.controller.addListener(_sync);
  }

  /// The threshold changed (another device, a refresh, this screen's own save): an untouched field follows it; one the user had edited is reset to it with a notice, so a stale amount is never saved.
  ///
  /// True when this call found the amount typed against another threshold (the field was reset and the warning is up).
  bool _sync() {
    final now = widget.controller.settings?.thresholdEur;
    if (now == null || now == _shown || !mounted) return false;
    final dirty = _text.text.trim() != _shown;
    _shown = now;
    setState(() {
      _text.text = now;
      _problem = null;
      _changedUnderneath = dirty;
    });
    return dirty;
  }

  @override
  void dispose() {
    widget.controller.removeListener(_sync);
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
    // The amount in the field was typed against another threshold: nothing is sent.
    if (_sync()) return;
    // The warning (if still up) was about an earlier reset; this amount is the user's own now.
    if (_changedUnderneath) setState(() => _changedUnderneath = false);
    final v = _typed;
    if (v == null) {
      setState(() => _problem = TrustStrings.of(Localizations.maybeLocaleOf(context)).t('amountProblem'));
      return;
    }
    final ok = await c.apply({'threshold_eur': v});
    if (ok && mounted && c.settings != null) {
      // The server's own spelling of the amount (`250.00`).
      _shown = c.settings!.thresholdEur;
      setState(() {
        _text.text = _shown;
        _changedUnderneath = false;
      });
    }
  }

  /// Turning a protection off loosens it: explained and confirmed first (the phone then asks for the fingerprint or face). Turning it on applies at once.
  Future<void> _confirmOff(SettingsController c, Map<String, Object?> change,
      String titleKey, String bodyKey) async {
    final s = TrustStrings.of(Localizations.maybeLocaleOf(context));
    final yes = await confirmSheet(context,
        title: s.t(titleKey),
        message: s.t(bodyKey),
        confirmLabel: s.t('turnOff'),
        cancelLabel: s.t('keepOn'),
        biometric: true,
        icon: Icons.fingerprint);
    if (yes) await c.apply(change);
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.controller;
    final s = TrustStrings.of(Localizations.maybeLocaleOf(context));
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
        if (_changedUnderneath) ...[
          const SizedBox(height: AppSpacing.s),
          StatusBanner(
            key: const Key('threshold-changed'),
            severity: StatusSeverity.warning,
            title: s.t('thresholdChangedTitle'),
            message: s.t('thresholdChangedBody'),
          ),
        ],
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
                        _changedUnderneath = false;
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
          onChanged: (_) => setState(() {
            _problem = null;
            _changedUnderneath = false;
          }),
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
              ? (v) => v
                  ? c.apply({'ask_on_test_networks': true})
                  : _confirmOff(c, {'ask_on_test_networks': false},
                      'askTestNetworksOffTitle', 'askTestNetworksOffBody')
              : null,
        ),
        SwitchListTile(
          key: const Key('block-unproven'),
          contentPadding: EdgeInsets.zero,
          title: Text(s.t('blockUnproven')),
          subtitle: Text(s.t('blockUnprovenHelp')),
          value: settings.blockUnprovenClients,
          onChanged: enabled
              ? (v) => v
                  ? c.apply({'block_unproven_clients': true})
                  : _confirmOff(c, {'block_unproven_clients': false},
                      'blockUnprovenOffTitle', 'blockUnprovenOffBody')
              : null,
        ),
      ]),
    );
  }
}
