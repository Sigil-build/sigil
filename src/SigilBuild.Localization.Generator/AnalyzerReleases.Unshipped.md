### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SIGLOC001 | Localization | Error | Key in a translation that Strings.en.txt lacks.
SIGLOC002 | Localization | Warning | Key in Strings.en.txt missing from a translation; falls back to English.
SIGLOC003 | Localization | Error | Placeholder set differs between Strings.en.txt and a translation.
SIGLOC004 | Localization | Error | Duplicate key within one catalog file.
SIGLOC005 | Localization | Error | Malformed catalog line (no '=', or an empty key).
SIGLOC006 | Localization | Error | Two distinct keys collide on one generated method name.
SIGLOC007 | Localization | Error | A placeholder is named for a reserved C# keyword.
