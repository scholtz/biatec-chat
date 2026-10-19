import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'approval_card.dart';
import 'approval_controller.dart';
import 'approval_services.dart';

/// One request: card, binding code entry, Approve and Deny.
class ApprovalScreen extends StatefulWidget {
  const ApprovalScreen({
    super.key,
    required this.services,
    required this.authReqId,
  });

  final ApprovalServices services;
  final String authReqId;

  @override
  State<ApprovalScreen> createState() => _ApprovalScreenState();
}

class _ApprovalScreenState extends State<ApprovalScreen> {
  late final ApprovalController _controller;
  final _code = TextEditingController();
  Timer? _ticker;

  @override
  void initState() {
    super.initState();
    _controller = widget.services.controllerFor(widget.authReqId)..load();
    // Redraws the countdown.
    _ticker = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
    });
  }

  @override
  void dispose() {
    _ticker?.cancel();
    _code.dispose();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Approval')),
      body: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) => _body(context),
      ),
    );
  }

  Widget _body(BuildContext context) {
    final c = _controller;
    final scheme = Theme.of(context).colorScheme;
    switch (c.phase) {
      case ApprovalPhase.loading:
        return const Center(child: CircularProgressIndicator());
      case ApprovalPhase.unavailable:
        return _Message(
          icon: c.offline ? Icons.cloud_off : Icons.error_outline,
          text: c.offline
              ? 'Offline. Approvals need a connection to Biatec OIDC.'
              : (c.error ?? 'This request is not available.'),
          action:
              OutlinedButton(onPressed: c.load, child: const Text('Try again')),
        );
      case ApprovalPhase.approved:
        return const _Message(icon: Icons.check_circle, text: 'Approved.');
      case ApprovalPhase.denied:
        return const _Message(icon: Icons.block, text: 'Denied.');
      case ApprovalPhase.codeFailed:
        return _Message(
          icon: Icons.gpp_bad,
          text: 'Three wrong codes. The request was refused.'
              '${c.error == null ? '' : '\n${c.error}'}',
        );
      case ApprovalPhase.expired:
        return const _Message(
            icon: Icons.timer_off, text: 'This request has expired.');
      case ApprovalPhase.ready:
      case ApprovalPhase.working:
        final request = c.request!;
        final left = request.expiresAt.difference(c.now);
        final busy = c.phase == ApprovalPhase.working;
        return ListView(padding: const EdgeInsets.all(16), children: [
          ApprovalCard(request: request),
          const SizedBox(height: 8),
          Text(
            left.isNegative
                ? 'Expired'
                : 'Expires in ${left.inMinutes}:'
                    '${left.inSeconds.remainder(60).toString().padLeft(2, '0')}',
            key: const Key('expires-in'),
          ),
          const SizedBox(height: 16),
          TextField(
            key: const Key('binding-code'),
            controller: _code,
            enabled: !busy,
            keyboardType: TextInputType.number,
            maxLength: 6,
            inputFormatters: [FilteringTextInputFormatter.digitsOnly],
            decoration: InputDecoration(
              labelText: 'Code shown where you started this action',
              helperText: switch (c.attemptsRemaining) {
                null => 'Three attempts in total',
                1 => '1 attempt left',
                final n => '$n attempts left',
              },
            ),
          ),
          if (c.error != null) ...[
            const SizedBox(height: 8),
            Row(children: [
              if (c.offline) ...[
                Icon(Icons.cloud_off, color: scheme.error),
                const SizedBox(width: 8),
              ],
              Expanded(
                child: Text(
                  c.offline
                      ? 'Offline. Nothing was approved. ${c.error}'
                      : c.error!,
                  key: const Key('approval-error'),
                  style: TextStyle(color: scheme.error),
                ),
              ),
            ]),
          ],
          const SizedBox(height: 16),
          if (busy) const Center(child: CircularProgressIndicator()),
          if (!busy)
            Row(children: [
              Expanded(
                child: OutlinedButton(
                  key: const Key('deny'),
                  onPressed: c.deny,
                  child: const Text('Deny'),
                ),
              ),
              const SizedBox(width: 16),
              Expanded(
                child: FilledButton(
                  key: const Key('approve'),
                  onPressed: () => c.approve(_code.text),
                  child: const Text('Approve'),
                ),
              ),
            ]),
        ]);
    }
  }
}

class _Message extends StatelessWidget {
  const _Message({required this.icon, required this.text, this.action});

  final IconData icon;
  final String text;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Icon(icon, size: 48),
          const SizedBox(height: 16),
          Text(text,
              key: const Key('approval-message'), textAlign: TextAlign.center),
          if (action != null) ...[const SizedBox(height: 16), action!],
        ]),
      ),
    );
  }
}
