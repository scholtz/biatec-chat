import 'package:material_ui/material_ui.dart';

import '../security/settings_client.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'label_rules.dart';
import 'service_identity.dart';
import 'trust_strings.dart';
import 'trust_widgets.dart';

/// One linked service: how it is shown, the user's own label, "Always ask for this service", its own threshold, the applications it may call without asking, its pauses, and Unlink.
///
/// Every change goes to Biatec OIDC as it is; the screen never decides what loosens. A change Biatec OIDC will not take unsigned is signed with the approval key (one biometric prompt) by the
/// controller. An inactive device sees all of it, and can change none of it.
class ClientDetailScreen extends StatelessWidget {
  const ClientDetailScreen(
      {super.key,
      required this.services,
      required this.clientId,
      this.controller});

  final SettingsServices services;
  final String clientId;
  final SettingsController? controller;

  static MaterialPageRoute<void> route(
          SettingsServices services, SettingsController? controller, String id) =>
      MaterialPageRoute<void>(
        settings: RouteSettings(name: '/settings/clients/$id'),
        builder: (_) => ClientDetailScreen(
            services: services, controller: controller, clientId: id),
      );

  @override
  Widget build(BuildContext context) {
    final s = TrustStrings.of();
    return SettingsFrame(
      title: s.t('serviceTitle'),
      services: services,
      controller: controller,
      builder: (context, c) {
        final client = c.settings!.client(clientId);
        return [
          ...inactiveBanner(c),
          if (client == null)
            Padding(
              key: const Key('service-gone'),
              padding: const EdgeInsets.all(AppSpacing.l),
              child: Text(s.t('serviceGone'), textAlign: TextAlign.center),
            )
          else
            _Detail(
                key: Key('detail-$clientId'),
                client: client,
                account: c.settings!,
                controller: c),
        ];
      },
    );
  }
}

class _Detail extends StatelessWidget {
  const _Detail(
      {super.key,
      required this.client,
      required this.account,
      required this.controller});

  final ClientSetting client;
  final ConsentSettings account;
  final SettingsController controller;

  String get id => client.clientId;

  Map<String, Object?> _one(Map<String, Object?> member) => {
        'clients': {id: member}
      };

  Future<void> _editLabel(BuildContext context) async {
    final value = await _askLabel(context, displaySafeLabel(client.label) ?? '');
    if (value == null) return;
    await controller.apply(_one({'label': value}));
  }

  Future<void> _editThreshold(BuildContext context) async {
    final s = TrustStrings.of();
    final own = client.perClientThresholdEur;
    final v = await askAmount(context,
        title: s.t('serviceThresholdTitle'),
        initial: own ?? account.thresholdEur,
        current: own ?? account.thresholdEur);
    if (v == null) return;
    await controller.apply(_one({'per_client_threshold_eur': v}));
  }

  Future<void> _unlink(BuildContext context) async {
    final s = TrustStrings.of();
    final yes = await confirmSheet(context,
        title: s.t('unlinkTitle'),
        message: s.t('unlinkBody'),
        confirmLabel: s.t('unlink'),
        cancelLabel: s.t('keepLinked'),
        confirmKey: const Key('confirm-unlink'),
        icon: Icons.link_off);
    if (!yes) return;
    final done = await controller.apply(_one({'unlink': true}));
    if (done && context.mounted) Navigator.of(context).maybePop();
  }

  @override
  Widget build(BuildContext context) {
    final s = TrustStrings.of();
    final theme = Theme.of(context);
    final enabled = !controller.busy && controller.deviceActive;
    final own = client.perClientThresholdEur;
    final linked = client.linkedAt;
    return Padding(
      padding: const EdgeInsets.all(AppSpacing.m),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        ServiceIdentity.setting(client),
        const SizedBox(height: AppSpacing.xs),
        Text(
            linked == null
                ? s.t('notLinkedDate')
                : s.t('linkedOn', {'when': whenText(linked)}),
            style: theme.textTheme.bodySmall
                ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
        const Divider(height: AppSpacing.xl),
        // The user's own label.
        Text(s.t('yourLabel'), style: theme.textTheme.titleSmall),
        const SizedBox(height: AppSpacing.xs),
        Text(displaySafeLabel(client.label) ?? s.t('noLabel'),
            key: const Key('label-value'),
            style: theme.textTheme.bodyMedium),
        const SizedBox(height: AppSpacing.s),
        OutlinedButton(
          key: const Key('label-edit'),
          onPressed: enabled ? () => _editLabel(context) : null,
          child: Text(s.t('editLabel')),
        ),
        const Divider(height: AppSpacing.xl),
        SwitchListTile(
          key: const Key('always-ask'),
          contentPadding: EdgeInsets.zero,
          title: Text(s.t('alwaysAskService')),
          subtitle: Text(s.t('alwaysAskServiceHelp')),
          value: client.alwaysAsk,
          onChanged:
              enabled ? (v) => controller.apply(_one({'always_ask': v})) : null,
        ),
        const Divider(height: AppSpacing.xl),
        Text(s.t('serviceThresholdTitle'), style: theme.textTheme.titleSmall),
        const SizedBox(height: AppSpacing.xs),
        Text(
            own == null
                ? s.t('usesMyThreshold', {'amount': thresholdText(account.thresholdEur)})
                : thresholdText(own),
            key: const Key('service-threshold'),
            style: theme.textTheme.bodyMedium),
        const SizedBox(height: AppSpacing.s),
        OutlinedButton(
          key: const Key('threshold-edit'),
          onPressed: enabled ? () => _editThreshold(context) : null,
          child: Text(s.t('changeThreshold')),
        ),
        if (own != null)
          TextButton(
            key: const Key('threshold-reset'),
            onPressed: enabled
                ? () => controller
                    .apply(_one({'per_client_threshold_eur': null}))
                : null,
            child: Text(s.t('useMyThreshold')),
          ),
        const Divider(height: AppSpacing.xl),
        Text(s.t('trustedApplications'), style: theme.textTheme.titleSmall),
        const SizedBox(height: AppSpacing.xs),
        if (client.trustedApplications.isEmpty)
          Text(s.t('noTrustedApplications'),
              key: const Key('no-trusted-applications'),
              style: theme.textTheme.bodyMedium)
        else
          for (final app in client.trustedApplications)
            ListTile(
              key: Key('app-${app.network}-${app.appId}'),
              contentPadding: EdgeInsets.zero,
              title: Text(s.t('applicationOn',
                  {'id': '${app.appId}', 'network': app.network})),
              trailing: IconButton(
                key: Key('remove-app-${app.network}-${app.appId}'),
                tooltip: s.t('removeApplication'),
                icon: const Icon(Icons.delete_outline),
                onPressed: enabled
                    ? () => controller.apply(
                        _one({'trusted_applications_remove': [app.toJson()]}))
                    : null,
              ),
            ),
        const Divider(height: AppSpacing.xl),
        Text(s.t('pauses'), style: theme.textTheme.titleSmall),
        const SizedBox(height: AppSpacing.xs),
        if (client.pausedUntil == null)
          Text(s.t('notPaused'),
              key: const Key('not-paused'), style: theme.textTheme.bodyMedium)
        else if (client.pauses.isEmpty)
          StatusBanner(
            key: Key('paused-$id'),
            severity: StatusSeverity.warning,
            icon: Icons.pause_circle_outline,
            title: s.t('pausedUntil', {'when': whenText(client.pausedUntil!)}),
            message: s.t('pausedBody'),
          )
        else
          for (final pause in client.pauses)
            Padding(
              padding: const EdgeInsets.only(bottom: AppSpacing.s),
              child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    StatusBanner(
                      key: Key('paused-$id-${pause.origin}'),
                      severity: StatusSeverity.warning,
                      icon: Icons.pause_circle_outline,
                      title: s.t('pausedOrigin', {
                        'origin': pause.origin,
                        'when': whenText(pause.pausedUntil)
                      }),
                      message: s.t('pausedBody'),
                    ),
                    const SizedBox(height: AppSpacing.s),
                    FilledButton(
                      key: Key('lift-$id-${pause.origin}'),
                      onPressed: enabled
                          ? () => liftPause(context, controller, id, pause)
                          : null,
                      child: Text(s.t('liftPause')),
                    ),
                  ]),
            ),
        const Divider(height: AppSpacing.xl),
        OutlinedButton.icon(
          key: const Key('unlink'),
          style: OutlinedButton.styleFrom(
              foregroundColor: theme.colorScheme.error),
          onPressed: enabled ? () => _unlink(context) : null,
          icon: const Icon(Icons.link_off),
          label: Text(s.t('unlink')),
        ),
        const SizedBox(height: AppSpacing.xs),
        Text(s.t('unlinkHelp'),
            style: theme.textTheme.bodySmall
                ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
      ]),
    );
  }
}

/// The sheet that asks for the user's own label. The same text rules as Biatec OIDC ([labelProblem]) are applied before anything is sent, and said as the user types.
Future<String?> _askLabel(BuildContext context, String initial) =>
    showModalBottomSheet<String>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => _LabelSheet(initial: initial),
    );

class _LabelSheet extends StatefulWidget {
  const _LabelSheet({required this.initial});

  final String initial;

  @override
  State<_LabelSheet> createState() => _LabelSheetState();
}

class _LabelSheetState extends State<_LabelSheet> {
  late final TextEditingController _text =
      TextEditingController(text: widget.initial);

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  String? _problemText(TrustStrings s) {
    final text = _text.text;
    if (text.isEmpty) return null;
    return switch (labelProblem(text)) {
      null => null,
      LabelProblem.empty => s.t('labelEmpty'),
      LabelProblem.tooLong => s.t('labelTooLong', {'max': '$labelMaxLength'}),
      LabelProblem.padded => s.t('labelPadded'),
      LabelProblem.notAllowed => s.t('labelNotAllowed'),
    };
  }

  @override
  Widget build(BuildContext context) {
    final s = TrustStrings.of();
    final theme = Theme.of(context);
    final problem = _problemText(s);
    final valid = _text.text.isNotEmpty && labelProblem(_text.text) == null;
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
                child: Text(s.t('yourLabel'), style: theme.textTheme.titleLarge)),
            const SizedBox(height: AppSpacing.m),
            TextField(
              key: const Key('label-field'),
              controller: _text,
              autofocus: true,
              onChanged: (_) => setState(() {}),
              onSubmitted: (_) {
                if (valid) Navigator.of(context).pop(_text.text);
              },
              decoration: InputDecoration(
                labelText: s.t('labelField'),
                helperText: s.t('labelHelp', {'max': '$labelMaxLength'}),
                helperMaxLines: 3,
                errorText: problem,
                errorMaxLines: 3,
                counterText: '${_text.text.runes.length}/$labelMaxLength',
              ),
            ),
            const SizedBox(height: AppSpacing.l),
            FilledButton(
                key: const Key('label-save'),
                onPressed: valid ? () => Navigator.of(context).pop(_text.text) : null,
                child: Text(s.t('save'))),
            const SizedBox(height: AppSpacing.s),
            OutlinedButton(
                key: const Key('label-cancel'),
                onPressed: () => Navigator.of(context).pop(),
                child: Text(s.t('cancel'))),
          ],
        ),
      ),
    );
  }
}
