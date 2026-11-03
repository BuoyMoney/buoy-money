import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/ai_settings_page.dart';
import 'package:buoy_ai/services/ai_status_service.dart';
import 'package:buoy_ai/widgets/cinematic/glass_settings.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

class _FakeService extends AiStatusService {
  _FakeService(this.status, {this.steps = const [], this.fail = false});

  final AiStatus status;
  final List<AiProbeStep> steps;
  final bool fail;
  int probes = 0;

  @override
  Future<AiStatus> fetch() async {
    if (fail) throw Exception('404');
    return status;
  }

  @override
  Future<List<AiProbeStep>> probe() async {
    probes++;
    return steps;
  }
}

Widget _wrap(AiStatusService service) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: AiSettingsPage(service: service),
    );

const _features = [
  AiFeatureStatus(id: 'categorization', name: 'Transaction categorization', status: 'works', note: 'Works on most models of 7B and up.'),
  AiFeatureStatus(id: 'insights', name: 'Dashboard insights', status: 'limited', note: 'Needs 14B or larger.'),
  AiFeatureStatus(id: 'chat', name: 'Chat that looks things up in your data', status: 'off', note: 'Runs on Gemini only for now.'),
];

void main() {
  testWidgets('shows the provider and an honest matrix: works, limited, off', (tester) async {
    final service = _FakeService(const AiStatus(
      enabled: true,
      provider: 'ollama',
      edition: 'selfhost',
      features: _features,
    ));

    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    expect(find.text('AI is on'), findsOneWidget);
    expect(find.textContaining('Ollama'), findsOneWidget);
    expect(find.text('Transaction categorization'), findsOneWidget);
    expect(find.text('Works'), findsOneWidget);
    expect(find.text('Limited'), findsOneWidget);
    expect(find.text('Off'), findsOneWidget);
    expect(find.text('Runs on Gemini only for now.'), findsOneWidget);
    // A non-admin sees no connection details or check button.
    expect(find.text('Check the connection'), findsNothing);
  });

  testWidgets('AI off explains how to turn it on and names the missing setting', (tester) async {
    final service = _FakeService(const AiStatus(
      enabled: false,
      provider: 'ollama',
      edition: 'selfhost',
      features: [
        AiFeatureStatus(id: 'categorization', name: 'Transaction categorization', status: 'off', note: 'Not set up: Ai:Model is not set.'),
      ],
      details: AiProviderDetails(baseUrl: 'http://localhost:11434/v1', model: '', setupProblem: 'Ai:Model is not set.'),
    ));

    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    expect(find.text('AI is off'), findsOneWidget);
    expect(find.text('Ai:Model is not set.'), findsOneWidget);
    expect(find.textContaining('Ai__Provider=ollama'), findsOneWidget);
    // The check can't run with AI off.
    await tester.dragUntilVisible(find.text('Check the connection'), find.byType(ListView), const Offset(0, -300));
    final button = tester.widget<GlassPrimaryButton>(find.byKey(const ValueKey('ai-set-check')));
    expect(button.onPressed, isNull);
  });

  testWidgets('an administrator can run the live check and sees each step', (tester) async {
    final service = _FakeService(
      const AiStatus(
        enabled: true,
        provider: 'ollama',
        edition: 'selfhost',
        features: _features,
        details: AiProviderDetails(baseUrl: 'http://localhost:11434/v1', model: 'qwen2.5:7b'),
      ),
      steps: const [
        AiProbeStep(name: 'Reach the model', ok: true, detail: 'The model answered.', milliseconds: 420),
        AiProbeStep(name: 'Answers in the categorization format', ok: false, detail: 'The reply could not be read.', milliseconds: 900),
      ],
    );

    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    await tester.dragUntilVisible(find.text('Check the connection'), find.byType(ListView), const Offset(0, -300));
    expect(find.text('qwen2.5:7b'), findsOneWidget);
    await tester.ensureVisible(find.text('Check the connection'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Check the connection'));
    await tester.pumpAndSettle();

    expect(service.probes, 1);
    await tester.dragUntilVisible(find.text('Reach the model'), find.byType(ListView), const Offset(0, -300));
    expect(find.text('Reach the model'), findsOneWidget);
    expect(find.textContaining('The model answered.'), findsOneWidget);
    await tester.dragUntilVisible(
        find.textContaining('The reply could not be read.'), find.byType(ListView), const Offset(0, -300));
    expect(find.textContaining('The reply could not be read.'), findsOneWidget);
  });

  testWidgets('an older server without the endpoint gets a plain message and a retry', (tester) async {
    final service = _FakeService(
      const AiStatus(enabled: true, provider: 'gemini', edition: 'hosted', features: []),
      fail: true,
    );

    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    expect(find.textContaining('older than the app'), findsOneWidget);
    expect(find.text('Try again'), findsOneWidget);
  });
}
