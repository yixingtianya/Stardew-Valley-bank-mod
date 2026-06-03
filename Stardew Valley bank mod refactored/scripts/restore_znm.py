import subprocess, os

repo = r"F:\agency-agents-main\Stardew Valley bank mod refactored"
assets = os.path.join(repo, "assets")
backup = os.path.join(assets, "znm_backup")
os.makedirs(backup, exist_ok=True)

names = ["znm","znm1","znm2","znmf","znm1f","znm2f","znmh","znm1h","znm2h","znmz","znm1z","znm2z"]

for name in names:
    result = subprocess.run(["git", "show", f"HEAD:assets/{name}.png"],
                          cwd=repo, capture_output=True)
    if result.returncode == 0:
        bak_path = os.path.join(backup, f"{name}.png")
        with open(bak_path, "wb") as f:
            f.write(result.stdout)
        print(f"Backed up {name}.png into backup folder")
    else:
        print(f"FAILED {name}: {result.stderr.decode(errors='replace')}")

for name in names:
    bak_path = os.path.join(backup, f"{name}.png")
    asset_path = os.path.join(assets, f"{name}.png")
    if os.path.exists(bak_path):
        with open(bak_path, "rb") as f:
            data = f.read()
        with open(asset_path, "wb") as f:
            f.write(data)
        print(f"Restored original {name}.png")
print("Done - originals restored!")
