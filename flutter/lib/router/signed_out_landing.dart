import '../config/app_config.dart';
import '../config/first_run.dart';

/// Where the first-launch question lives.
const firstRunPath = '/get-started';

/// Where a signed-out visitor at the app root goes.
///
/// - A referral link (`/app/?ref=CODE`) is an invitation to join, so it goes to sign-up: a referral can only be
///   redeemed by a brand new account.
/// - A fresh install of the production phone app asks "How would you like to use Buoy Money?" first
///   ([FirstRun.shouldShow]).
/// - Someone who picked a server of their own signs in to it: its owner made their account there.
/// - Any other first arrival goes to sign-up. Of the first Play testers, ten reached /login and not one reached
///   /register, because a fresh install has no password yet. Someone who signed in here before gets /login.
String signedOutLanding({required bool hasSignedInBefore, bool hasReferral = false}) {
  if (hasReferral) return '/register';
  if (FirstRun.shouldShow(hasSignedInBefore: hasSignedInBefore)) return firstRunPath;
  if (AppConfig.isCustomServer) return '/login';
  return hasSignedInBefore ? '/login' : '/register';
}
