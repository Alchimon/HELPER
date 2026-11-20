#!/usr/bin/env bash
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
src=$(cd "$(dirname "$0")" && pwd)
root=$(mktemp -d)
raw=$PWD/gemini.raw
loop=''
cleanup() {
  for d in run sys proc dev; do umount -R "$root/$d" 2>/dev/null || true; done
  umount "$root" 2>/dev/null || true
  if [[ -n $loop ]]; then losetup -d "$loop"; fi
  rmdir "$root" || true
}
trap cleanup EXIT
truncate -s 8G "$raw"
parted -s "$raw" mklabel msdos mkpart primary ext4 1MiB 100% set 1 boot on
loop=$(losetup --find --show --partscan "$raw")
part=${loop}p1
udevadm settle
mkfs.ext4 -L GEMINI "$part"
mount "$part" "$root"
debootstrap --variant=minbase trixie "$root" https://deb.debian.org/debian
for d in dev proc sys; do mount --rbind "/$d" "$root/$d"; mount --make-rslave "$root/$d"; done
printf 'UUID=%s / ext4 defaults 0 1\n' "$(blkid -s UUID -o value "$part")" > "$root/etc/fstab"
printf 'gemini-vm\n' > "$root/etc/hostname"
printf '#!/bin/sh\nexit 101\n' > "$root/usr/sbin/policy-rc.d"
chmod +x "$root/usr/sbin/policy-rc.d"
chroot "$root" apt-get update
chroot "$root" apt-get install -y --no-install-recommends linux-image-amd64 grub-pc xserver-xorg xserver-xorg-legacy xinit xauth openbox chromium zenity thunar dbus-x11 libpam-systemd network-manager ca-certificates python3 fonts-dejavu gvfs policykit-1
printf 'allowed_users=anybody\nneeds_root_rights=yes\n' > "$root/etc/X11/Xwrapper.config"
chroot "$root" useradd -m -s /bin/bash gemini
chroot "$root" passwd -l gemini
install -d -o 1000 -g 1000 "$root/home/gemini/Downloads"
install -m 0755 "$src/files/kiosk.py" "$root/usr/local/bin/gemini-kiosk"
install -m 0755 "$src/files/session" "$root/usr/local/bin/gemini-session"
install -m 0644 "$src/files/gemini.service" "$root/etc/systemd/system/gemini.service"
install -m 0755 "$PWD/xray" "$root/usr/local/bin/xray"
# Linux kernel provides vboxsf; explicit systemd mount avoids a Guest Additions ISO.
install -d "$root/media/sf_Gemini-Exchange"
cat >> "$root/etc/fstab" <<'SHARE'
Gemini-Exchange /media/sf_Gemini-Exchange vboxsf noauto,x-systemd.automount,nofail,uid=1000,gid=1000,nodev,nosuid,noexec 0 0
SHARE
mkdir -p "$root/etc/NetworkManager/system-connections"
cat > "$root/etc/NetworkManager/system-connections/nat.nmconnection" <<'NET'
[connection]
id=Virtual Ethernet
 type=ethernet
[ipv4]
method=auto
[ipv6]
method=auto
NET
sed -i 's/^ type/type/' "$root/etc/NetworkManager/system-connections/nat.nmconnection"
chmod 600 "$root/etc/NetworkManager/system-connections/nat.nmconnection"
mkdir -p "$root/etc/polkit-1/rules.d"
cat > "$root/etc/polkit-1/rules.d/49-gemini.rules" <<'RULE'
polkit.addRule(function(action, subject) {
    if (action.id == 'org.freedesktop.login1.power-off' && subject.user == 'gemini' && subject.local) return polkit.Result.YES;
});
RULE
chroot "$root" systemctl enable gemini.service NetworkManager.service
chroot "$root" systemctl mask getty@tty1.service
chroot "$root" grub-install --target=i386-pc "$loop"
chroot "$root" update-grub
chroot "$root" apt-get clean
rm -f "$root/usr/sbin/policy-rc.d"
truncate -s 0 "$root/etc/machine-id"
sync
