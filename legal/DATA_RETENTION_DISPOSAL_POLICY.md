# Buoy Money Data Retention and Disposal Policy

**Document Version:** 1.0  
**Effective Date:** September 9, 2025  
**Next Review Date:** March 9, 2026  
**Policy Owner:** Buoy Money Security Team  
**Approval:** CTO/Chief Privacy Officer  

---

## 1. Purpose and Scope

This Data Retention and Disposal Policy establishes guidelines for the systematic retention, archival, and secure disposal of personal and financial data collected by Buoy Money in compliance with privacy laws (GDPR, CCPA) and financial regulations.

**Scope:** This policy applies to all data collected, processed, and stored by Buoy Money systems, including:
- User account information
- Financial transaction data (via Plaid)
- AI-generated insights and analysis
- System logs and audit trails
- Authentication and security data

---

## 2. Legal and Regulatory Framework

### 2.1 Compliance Requirements
- **GDPR Article 17** (Right to Erasure)
- **GDPR Article 5** (Data Minimization and Storage Limitation)
- **CCPA Section 1798.105** (Right to Delete)
- **PCI DSS** requirements for financial data
- **SOX** requirements for financial audit trails
- **Plaid** data security and retention requirements

### 2.2 Data Minimization Principle
We collect and retain only the minimum data necessary for:
- Providing financial management services
- Regulatory compliance
- Security monitoring and fraud prevention
- Legal obligations

---

## 3. Data Classification and Retention Schedules

### 3.1 User Account Data
| Data Type | Retention Period | Disposal Method | Legal Basis |
|-----------|------------------|-----------------|-------------|
| **Active Account Profile** | Duration of account | N/A - Active use | Service provision |
| **Closed Account Data** | 30 days | Automated secure deletion | GDPR compliance |
| **Password Hashes** | Account lifetime + 30 days | Cryptographic overwrite | Security |
| **Profile Pictures** | Account lifetime + 7 days | Secure file deletion | User preference |

### 3.2 Financial Data (Plaid Integration)
| Data Type | Retention Period | Disposal Method | Legal Basis |
|-----------|------------------|-----------------|-------------|
| **Transaction Records** | 2 years or account closure | Encrypted deletion | Financial regulations |
| **Account Balances** | Account active + 90 days | Secure database deletion | Service provision |
| **Plaid Access Tokens** | Token expiry or disconnect | Immediate cryptographic deletion | Security requirement |
| **Bank Account Numbers** | Account connection + 0 days | Immediate secure deletion | PCI DSS compliance |

### 3.3 AI and Analytics Data
| Data Type | Retention Period | Disposal Method | Legal Basis |
|-----------|------------------|-----------------|-------------|
| **AI Insights** | 1 year or user deletion | Automated archival/deletion | Service improvement |
| **Usage Analytics** | 2 years aggregated | Anonymization then deletion | Business analytics |
| **AI Training Data** | Anonymized permanently | N/A - Anonymized | Product improvement |

### 3.4 System and Security Logs
| Data Type | Retention Period | Disposal Method | Legal Basis |
|-----------|------------------|-----------------|-------------|
| **Security Audit Logs** | 90 days | Automated log rotation | Security monitoring |
| **Error Logs** | 30 days | Automated deletion | System maintenance |
| **Access Logs** | 90 days | Secure log archival | Compliance auditing |
| **Failed Login Attempts** | 30 days | Automated purge | Security analysis |

### 3.5 Authentication and Device Trust Data
| Data Type | Retention Period | Disposal Method | Legal Basis |
|-----------|------------------|-----------------|-------------|
| **Trusted Device Records** | 30 days from last use or revocation | Automated deletion | Security feature |
| **Device Fingerprints** | Duration of device trust | SHA-256 hash - secure deletion | Authentication |
| **Login Attempt History** | 90 days | Automated purge | Security monitoring |
| **MFA Session Tokens** | 5 minutes (admin) to 30 minutes (financial) | Automatic expiration | Security requirement |
| **Step-Up Auth Codes** | 10 minutes or verification | Immediate deletion after use | Security protocol |

### 3.6 Payment and Billing Data (Stripe Integration)

**Important:** Buoy Money does not store credit card numbers, CVV codes, or full card details. We use Stripe as our payment processor and only store tokenized references.

| Data Type | Retention Period | Disposal Method | Legal Basis |
|-----------|------------------|-----------------|-------------|
| **Stripe Customer IDs** | Account lifetime + 30 days | Secure deletion + Stripe API deletion | Service provision |
| **Stripe Subscription IDs** | Subscription lifetime + 7 years | Secure archival for tax compliance | Tax regulations |
| **Payment Method Tokens** | Payment method lifetime + 30 days | Secure deletion + Stripe API detachment | PCI DSS compliance |
| **Invoice Records** | 7 years | Secure archival | Tax and audit requirements |
| **Payment History** | 7 years | Secure archival | Financial regulations |
| **Failed Payment Logs** | 90 days | Automated deletion | Security monitoring |
| **Promo Code Usage** | 7 years | Secure archival | Tax compliance |

#### Stripe Data Security Model
- **No Card Data Storage:** Credit card numbers, CVV codes, and expiration dates are never stored in Buoy Money systems
- **Tokenization:** All payment data is handled via Stripe's PCI Level 1 compliant infrastructure
- **Reference Storage Only:** We store Stripe IDs (Customer, Subscription, PaymentMethod) as secure references
- **Stripe Elements:** Card information is collected via Stripe-hosted secure iframes
- **PCI Compliance:** Buoy Money qualifies for SAQ A (simple questionnaire) due to tokenization approach

#### Payment Data Deletion Process
When a user account is deleted:
1. **Buoy Money Database:** Stripe reference IDs removed within 30 days
2. **Stripe Customer Deletion:** Customer object deleted via Stripe API
3. **Payment Method Removal:** All payment methods detached and removed
4. **Subscription Cancellation:** Active subscriptions canceled immediately
5. **Invoice Retention:** Invoice records archived for 7 years (tax requirements)
6. **Stripe Data Retention:** Stripe handles their own data retention per their privacy policy

#### User Rights for Payment Data
- **Right to Access:** Users can request payment history via API or customer support
- **Right to Deletion:** Account deletion includes Stripe Customer deletion within 30 days
- **Right to Portability:** Payment history and invoice data exportable in JSON/CSV format
- **Right to Correction:** Payment method updates handled via Stripe's secure flows

### 3.6 Legal and Compliance Data
| Data Type | Retention Period | Disposal Method | Legal Basis |
|-----------|------------------|-----------------|-------------|
| **Data Deletion Requests** | 3 years | Secure archival | Legal proof |
| **Privacy Rights Requests** | 3 years | Secure archival | Compliance documentation |
| **Consent Records** | 7 years | Secure archival | Legal requirement |

---

## 4. Automated Retention Enforcement

### 4.1 Automated Deletion System
- **Background Service:** `DataRetentionService` runs daily at 2:00 AM UTC
- **Retention Checker:** Identifies data exceeding retention periods
- **Secure Deletion:** Cryptographically secure deletion methods
- **Audit Trail:** All deletions logged for compliance verification

### 4.2 Deletion Methods by Data Type

#### 4.2.1 Database Records
- **Method:** Multi-pass overwrite with random data
- **Verification:** Deletion confirmation queries
- **Logging:** Audit trail with timestamps and record counts

#### 4.2.2 Encrypted Files
- **Method:** Cryptographic key destruction + file overwrite
- **Verification:** File system verification
- **Logging:** File deletion audit logs

#### 4.2.3 Log Files
- **Method:** Secure log rotation and deletion
- **Verification:** Log file system checks
- **Logging:** System log rotation records

### 4.3 Emergency Deletion Procedures
- **User Request:** Immediate deletion within 24 hours
- **Legal Hold:** Suspension of automated deletion
- **Security Incident:** Expedited deletion if required

---

## 5. Data Subject Rights and Deletion Requests

### 5.1 User-Initiated Deletion
- **Account Closure:** User can close account via application
- **Selective Deletion:** Users can delete specific data types
- **Data Export:** Users can export their data (JSON or CSV) before deletion, in the app under Settings > Privacy & Data > Download my data
- **Verification:** Email confirmation required for deletion requests

### 5.2 Deletion Request Processing
1. **Request Received:** Via privacy@buoy-ai.com or in-app
2. **Identity Verification:** Multi-factor authentication required
3. **Deletion Execution:** Within 30 days maximum
4. **Confirmation:** User notified of completion
5. **Audit Record:** Deletion logged for compliance

### 5.3 Right to Be Forgotten (GDPR Article 17)
- **Immediate Processing:** No undue delay
- **Third-Party Notification:** Plaid and other processors notified
- **Exemptions:** Legal obligations and legitimate interests respected
- **Documentation:** All requests documented and tracked

---

## 6. Technical Implementation

### 6.1 Automated Systems
Enforcement is a **single** `DataRetentionService` (a .NET `BackgroundService` in
`backend/src/BuoyAI.Infrastructure/Services/DataRetentionService.cs`) that wakes once daily at
**02:00 UTC** and runs the following cleanup steps via `IDataRetentionManager`:

```
DataRetentionService (BackgroundService, daily @ 02:00 UTC)
├── CleanupClosedUserAccountsAsync        (closed accounts, 30 days)
├── CleanupSecurityLogsAsync              (security logs, 90 days)
├── CleanupErrorLogsAsync                 (error logs, 30 days)
├── CleanupOldAIInsightsAsync             (AI insights, 1 year)
├── CleanupAIRefreshUsageAsync            (AI refresh usage, 90 days)
├── CleanupDisconnectedAccountDataAsync   (disconnected-account transactions, immediate)
├── CleanupExpiredPlaidTokensAsync        (expired Plaid tokens, on expiry)
└── CleanupExpiredPasskeyChallengesAsync  (expired passkey challenges, ~1 hour)
```

**Stripe customer deletion.** Whenever user data is deleted — via the nightly
`CleanupClosedUserAccountsAsync` sweep (30 days after closure) *or* an immediate
`DeleteUserDataImmediatelyAsync` request — `DataRetentionManager` also deletes the user's
**Stripe Customer** via the Stripe API. Deleting the customer cascades on Stripe's side: it
cancels active subscriptions and detaches saved payment methods, satisfying §3.6.

Two properties worth knowing:
- **Fail-open.** A Stripe API failure never blocks or rolls back the GDPR/CCPA-required local
  deletion. The failure is logged and audited as `STRIPE_CUSTOMER_DELETE_FAILED` **with the
  customer id**, so an orphaned Stripe customer can be reconciled by hand.
- **Invoices survive by design.** Stripe retains invoice/payment records under its own
  retention policy for the tax/audit periods in §3.6; deleting the Customer does not erase them.

### 6.2 Deletion Verification
- **Database Constraints:** Cascade deletions properly configured
- **Referential Integrity:** Foreign key relationships respected
- **Backup Purging:** Deleted data removed from backups
- **Cache Clearing:** In-memory caches invalidated

### 6.3 Security Measures
- **Encryption at Rest:** AES-256 encryption before deletion
- **Secure Deletion Standards:** NIST 800-88 compliance
- **Access Controls:** Deletion operations require admin privileges
- **Monitoring:** All deletion operations monitored and alerted

---

## 7. Compliance Monitoring and Auditing

### 7.1 Regular Audits
- **Quarterly Reviews:** Data retention compliance checks
- **Annual Assessments:** Full policy compliance audit
- **External Audits:** Third-party compliance verification
- **Continuous Monitoring:** Automated compliance reporting

### 7.2 Audit Trail Requirements
- **Deletion Records:** Date, time, user, data type, record count
- **Access Logs:** Who accessed deletion systems
- **Compliance Reports:** Regular retention compliance status
- **Exception Reporting:** Any deviations from policy

### 7.3 Key Performance Indicators (KPIs)
- **Retention Compliance Rate:** >99.5% target
- **Deletion Request Response Time:** <24 hours average
- **Automated Deletion Success Rate:** >99.9% target
- **Audit Finding Resolution:** <30 days average

---

## 8. Exceptions and Legal Holds

### 8.1 Legal Hold Procedures
- **Litigation Hold:** Suspend deletion for legal proceedings
- **Regulatory Investigation:** Preserve data for authorities
- **Security Incident:** Retain data for forensic analysis
- **Documentation:** All holds documented with legal justification

### 8.2 Exception Handling
- **Business Continuity:** Critical system data protection
- **Regulatory Requirements:** Extended retention for compliance
- **User Consent:** Extended retention with explicit consent
- **Technical Limitations:** Temporary retention for technical reasons

---

## 9. Training and Awareness

### 9.1 Staff Training
- **Data Retention Training:** All staff annually
- **Privacy Rights Training:** Customer service team quarterly
- **Technical Training:** Engineering team on deletion procedures
- **Compliance Updates:** Regular policy updates communicated

### 9.2 Documentation and Resources
- **Policy Documentation:** Accessible to all staff
- **Procedure Manuals:** Detailed technical procedures
- **Decision Trees:** Data retention decision flowcharts
- **Contact Information:** Privacy team contact details

---

## 10. Policy Review and Updates

### 10.1 Review Schedule
- **Quarterly:** Retention schedules and technical procedures
- **Semi-Annual:** Legal and regulatory compliance
- **Annual:** Full policy review and update
- **Ad-Hoc:** Changes in law or business requirements

### 10.2 Change Management
- **Version Control:** All policy versions tracked
- **Approval Process:** Changes require CTO and legal approval
- **Communication:** Policy changes communicated to all staff
- **Implementation:** Updated procedures implemented within 30 days

### 10.3 Continuous Improvement
- **Feedback Collection:** Regular feedback from staff and users
- **Best Practices:** Industry best practice incorporation
- **Technology Updates:** New deletion technologies evaluated
- **Compliance Evolution:** Adapt to changing regulations

---

## 11. Contact Information

### 11.1 Policy Contacts
- **Data Protection Officer:** privacy@buoy-ai.com
- **Legal Compliance:** legal@buoy-ai.com
- **Audit and Compliance:** audit@buoy-ai.com

### 11.2 User Rights Requests
- **Email:** privacy@buoy-ai.com
- **Subject Line:** "Data Retention Request"
- **Response Time:** Within 72 hours
- **Escalation:** legal@buoy-ai.com for complex cases

---

## 12. Appendices

### Appendix A: Data Flow Diagram
[Technical diagram showing data lifecycle and deletion points]

### Appendix B: Legal Requirements Matrix
[Detailed mapping of legal requirements to retention periods]

### Appendix C: Technical Deletion Procedures
[Step-by-step technical procedures for each data type]

### Appendix D: Compliance Checklist
[Quarterly compliance verification checklist]

---

**Document Control:**
- **Document ID:** BUO-POL-DRD-001
- **Classification:** Internal Use
- **Distribution:** All Staff, Legal Team, External Auditors
- **Review Cycle:** Annual with quarterly updates
- **Approval Date:** September 9, 2025
- **Next Review:** March 9, 2026

---

*This policy is effective immediately and supersedes all previous data retention policies. Any questions should be directed to the Data Protection Officer at privacy@buoy-ai.com.*
