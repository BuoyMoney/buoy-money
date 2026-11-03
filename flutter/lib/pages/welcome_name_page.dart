import '../l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import '../router/post_auth_redirect.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import '../providers/auth_provider.dart';
import '../services/user_service.dart';
import '../widgets/cinematic/noise_overlay.dart';
import '../widgets/cinematic/backgrounds/interactive_background_layer.dart';

/// Shown after a social sign-in that provided no name — Apple never shares a name
/// on the web/return visits, and "Hide My Email" users have no usable email prefix
/// either. The router gates authenticated name-less users here so the app can greet
/// them properly, mirroring what the plain register form already requires.
class WelcomeNamePage extends StatefulWidget {
  const WelcomeNamePage({super.key});

  @override
  State<WelcomeNamePage> createState() => _WelcomeNamePageState();
}

class _WelcomeNamePageState extends State<WelcomeNamePage> {
  final _formKey = GlobalKey<FormState>();
  final _firstNameController = TextEditingController();
  final _lastNameController = TextEditingController();
  final _mousePos = ValueNotifier<Offset>(Offset.zero);
  final _userService = UserService();
  bool _saving = false;
  String? _error;

  @override
  void dispose() {
    _firstNameController.dispose();
    _lastNameController.dispose();
    _mousePos.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final last = _lastNameController.text.trim();
      await _userService.updateProfile(UpdateProfileRequest(
        firstName: _firstNameController.text.trim(),
        lastName: last.isEmpty ? null : last,
      ));
      if (!mounted) return;
      // Reload the user so the router's name-gate clears and lets us into the app.
      await context.read<AuthProvider>().refreshUser();
      if (!mounted) return;
      context.go(PostAuthRedirect.destination());
    } catch (e) {
      if (mounted) {
        setState(() {
          _error = 'Could not save your name. Please try again.';
          _saving = false;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      body: Stack(
        children: [
          Positioned.fill(
            child: InteractiveBackgroundLayer(mouseNotifier: _mousePos),
          ),
          const Positioned.fill(child: NoiseOverlay(opacity: 0.1)),
          Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 48),
              // Centre INSIDE the scroll view: wrapping it in Center makes it
              // shrink-wrap to the card, stranding the web scrollbar mid-page.
              child: Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 380),
                  child: Form(
                    key: _formKey,
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Text(
                          AppLocalizations.of(context).welcomeWhatShouldWeCallYou,
                          textAlign: TextAlign.center,
                          style: GoogleFonts.instrumentSerif(
                            fontSize: 34,
                            color: theme.colorScheme.onSurface,
                          ),
                        ),
                        const SizedBox(height: 12),
                        Text(
                          AppLocalizations.of(context).welcomeAddNameBody,
                          textAlign: TextAlign.center,
                          style: GoogleFonts.sora(
                            fontSize: 14,
                            color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                          ),
                        ),
                        const SizedBox(height: 32),
                        TextFormField(
                          controller: _firstNameController,
                          autofocus: true,
                          textInputAction: TextInputAction.next,
                          textCapitalization: TextCapitalization.words,
                          decoration: _decoration(theme, 'First name'),
                          validator: (v) => (v == null || v.trim().isEmpty)
                              ? 'Please enter your name'
                              : null,
                        ),
                        const SizedBox(height: 16),
                        TextFormField(
                          controller: _lastNameController,
                          textInputAction: TextInputAction.done,
                          textCapitalization: TextCapitalization.words,
                          decoration: _decoration(theme, 'Last name (optional)'),
                          onFieldSubmitted: (_) => _save(),
                        ),
                        if (_error != null) ...[
                          const SizedBox(height: 16),
                          Text(
                            _error!,
                            textAlign: TextAlign.center,
                            style: TextStyle(color: theme.colorScheme.error),
                          ),
                        ],
                        const SizedBox(height: 28),
                        FilledButton(
                          onPressed: _saving ? null : _save,
                          style: FilledButton.styleFrom(
                            padding: const EdgeInsets.symmetric(vertical: 16),
                            shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(16),
                            ),
                          ),
                          child: _saving
                              ? const SizedBox(
                                  width: 20,
                                  height: 20,
                                  child: CircularProgressIndicator(strokeWidth: 2),
                                )
                              : Text(
                                  AppLocalizations.of(context).welcomeContinue,
                                  style: GoogleFonts.sora(
                                    fontSize: 15,
                                    fontWeight: FontWeight.w600,
                                  ),
                                ),
                        ),
                        const SizedBox(height: 12),
                        TextButton(
                          onPressed: _saving
                              ? null
                              : () => context.read<AuthProvider>().logout(),
                          child: Text(
                            AppLocalizations.of(context).uiSignOut,
                            style: GoogleFonts.sora(
                              fontSize: 13,
                              color: theme.colorScheme.onSurface.withValues(alpha: 0.5),
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  InputDecoration _decoration(ThemeData theme, String label) => InputDecoration(
        labelText: label,
        filled: true,
        fillColor: theme.colorScheme.onSurface.withValues(alpha: 0.04),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide.none,
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide:
              BorderSide(color: theme.colorScheme.onSurface.withValues(alpha: 0.1)),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: theme.colorScheme.primary),
        ),
      );
}
