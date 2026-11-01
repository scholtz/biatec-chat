import 'package:flutter/material.dart';

import '../consent_request.dart';

/// The time the request was given, in seconds: what the requester asked for (`requested_expiry`), or five minutes when the request does not say.
int requestWindowSeconds(ConsentRequest request) {
  final v = request.json['requested_expiry'];
  return v is num && v > 0 ? v.round() : 300;
}

/// How much of the request's time is left: 1 when it was just issued, 0 when it has expired.
double expiryFraction(ConsentRequest request, DateTime now) {
  final left = request.expiresAt.difference(now).inMilliseconds / 1000;
  return (left / requestWindowSeconds(request)).clamp(0.0, 1.0);
}

/// `4:05`, for the approval screen.
String clockLeft(Duration left) {
  final s = left.isNegative ? 0 : left.inSeconds;
  return '${s ~/ 60}:${(s % 60).toString().padLeft(2, '0')}';
}

/// For lists, which redraw once in a while rather than every second.
String coarseLeft(Duration left) {
  if (left.isNegative || left == Duration.zero) return 'Expired';
  if (left.inMinutes < 1) return 'Less than a minute left';
  return '${left.inMinutes} min left';
}

/// In words, for a screen reader.
String spokenLeft(Duration left) {
  if (left.isNegative || left == Duration.zero) return 'expired';
  if (left.inMinutes < 1) return 'expires in less than a minute';
  final m = left.inMinutes;
  return 'expires in $m ${m == 1 ? 'minute' : 'minutes'}';
}

/// A ring that empties as the request runs out. Decorative: the time left is always also written, so the ring is hidden from a screen reader and never the only signal.
class ExpiryRing extends StatelessWidget {
  const ExpiryRing({super.key, required this.fraction, this.size = 40});

  final double fraction;
  final double size;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return ExcludeSemantics(
      child: SizedBox(
        width: size,
        height: size,
        child: Stack(alignment: Alignment.center, children: [
          SizedBox.expand(
            child: CircularProgressIndicator(
              value: fraction,
              strokeWidth: 4,
              backgroundColor: scheme.outlineVariant,
            ),
          ),
          Icon(Icons.timer_outlined, size: size * 0.45),
        ]),
      ),
    );
  }
}

/// Time left on the approval screen as words and a ring, never by the ring alone. A screen reader hears one sentence.
class ExpiryRow extends StatelessWidget {
  const ExpiryRow({super.key, required this.request, required this.now});

  final ConsentRequest request;
  final DateTime now;

  @override
  Widget build(BuildContext context) {
    final left = request.expiresAt.difference(now);
    final over = left <= Duration.zero;
    return Semantics(
      container: true,
      label: over ? 'Request expired' : 'Request ${spokenLeft(left)}',
      child: ExcludeSemantics(
        child: Row(children: [
          ExpiryRing(fraction: expiryFraction(request, now)),
          const SizedBox(width: 16),
          // Flexible: at large text the words wrap instead of running off the screen.
          Expanded(
            child: Text(
              over ? 'Expired' : 'Expires in ${clockLeft(left)}',
              key: const Key('expires-in'),
              style: Theme.of(context).textTheme.titleMedium,
            ),
          ),
        ]),
      ),
    );
  }
}
