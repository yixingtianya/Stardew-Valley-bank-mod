#!/usr/bin/env python3
"""Merge translated batch files back into en.json."""
import json
import os
from collections import OrderedDict

PROJECT_DIR = os.path.dirname(os.path.abspath(__file__))
I18N_DIR = os.path.join(PROJECT_DIR, 'i18n')

# Read current en.json
en_path = os.path.join(I18N_DIR, 'en.json')
with open(en_path, 'r', encoding='utf-8') as f:
    en_data = json.load(f, object_pairs_hook=lambda pairs: [(k, v) for k, v in pairs])

en_dict = dict(en_data)
updated = 0
skipped = 0

# Merge each batch file
for batch_num in range(1, 5):
    batch_path = os.path.join(PROJECT_DIR, f'_translation_batch_{batch_num}.json')
    if not os.path.exists(batch_path):
        print(f"Batch {batch_num}: file not found")
        continue

    with open(batch_path, 'r', encoding='utf-8') as f:
        batch = json.load(f)

    batch_updated = 0
    for key, entry in batch.items():
        if isinstance(entry, dict):
            english = entry.get('english', '')
            chinese = entry.get('chinese', '')
        else:
            continue

        if english and english != chinese:
            en_dict[key] = english
            batch_updated += 1
        else:
            skipped += 1

    updated += batch_updated
    print(f"Batch {batch_num}: {batch_updated} translations applied ({len(batch)} total entries)")

# Rebuild en.json preserving order
new_en = []
existing_keys = set()
for key, value in en_data:
    if key in en_dict:
        new_en.append((key, en_dict[key]))
        existing_keys.add(key)
    else:
        new_en.append((key, value))
        existing_keys.add(key)

# Add any keys from batch that weren't in original (shouldn't happen but safe)
for key, value in en_dict.items():
    if key not in existing_keys:
        new_en.append((key, value))

# Save
with open(en_path, 'w', encoding='utf-8') as f:
    json.dump(dict(new_en), f, ensure_ascii=False, indent=2)
    f.write('\n')

# Stats
empty_count = sum(1 for k, v in new_en if v == '')
print(f"\nSummary:")
print(f"  Total entries: {len(new_en)}")
print(f"  Filled with English: {updated}")
print(f"  Still empty: {empty_count}")
print(f"  Skipped (no change): {skipped}")
