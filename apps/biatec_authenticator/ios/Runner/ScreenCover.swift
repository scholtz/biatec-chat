import UIKit

/// Hides the app's content from the app switcher snapshot and while the screen is recorded or mirrored (BCHAT-031): a cover view is put over
/// every window of the app while the app is not active or the screen is captured, and removed again afterwards. Windows that appear while
/// the cover is up (a new scene, an alert, the keyboard) are covered as they become visible.
final class ScreenCover {
    static let shared = ScreenCover()
    /// Marks the cover views, so they are found again on the window itself (no bookkeeping that could go stale when a window goes away).
    private static let tag = 0x5C0_7E12
    private var inactive = false
    private var showing: Bool? = nil   // nil: no cover; false: plain (app inactive); true: with the "recorded or mirrored" message
    private var started = false

    func start() {
        guard !started else { return }
        started = true
        // The app can be started or restored while it is already inactive (Control Center, a call banner): no willResignActive follows.
        inactive = UIApplication.shared.applicationState != .active
        let center = NotificationCenter.default
        // applicationState is still .active inside willResignActive, so the state is tracked from the notifications themselves.
        center.addObserver(forName: UIApplication.willResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            self?.inactive = true
            self?.update()
        }
        center.addObserver(forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            self?.inactive = false
            self?.update()
        }
        center.addObserver(forName: UIScreen.capturedDidChangeNotification, object: nil, queue: .main) { [weak self] _ in self?.update() }
        center.addObserver(forName: UIWindow.didBecomeVisibleNotification, object: nil, queue: .main) { [weak self] _ in self?.update() }
        update()
    }

    private var windows: [UIWindow] {
        UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.flatMap { $0.windows }
    }

    private func update() {
        let captured = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.contains { $0.screen.isCaptured }
        guard inactive || captured else { return hide() }
        let message = captured && !inactive
        // A cover built for another reason (a plain one from going inactive, then a recording starts) is rebuilt; new windows are covered either way.
        if showing != message { hide() }
        show(captured: message)
        showing = message
    }

    private func show(captured: Bool) {
        for window in windows where window.viewWithTag(Self.tag) == nil {
            let cover = UIView(frame: window.bounds)
            cover.tag = Self.tag
            cover.autoresizingMask = [.flexibleWidth, .flexibleHeight]
            cover.backgroundColor = .systemBackground
            if captured {
                // The screen is being recorded or mirrored: say why the app is blank, or it looks like a crash.
                let label = UILabel(frame: cover.bounds.insetBy(dx: 24, dy: 0))
                label.autoresizingMask = [.flexibleWidth, .flexibleHeight]
                label.numberOfLines = 0
                label.textAlignment = .center
                label.textColor = .label
                label.text = "Hidden while the screen is recorded or mirrored. Stop the recording to continue."
                cover.addSubview(label)
            }
            window.addSubview(cover)
        }
    }

    private func hide() {
        for window in windows { window.viewWithTag(Self.tag)?.removeFromSuperview() }
        showing = nil
    }
}
