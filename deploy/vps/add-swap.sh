#!/bin/sh
# One-time: adds a 2 GB swap file. The VPS has 4 GB of RAM and no swap; SQL Server, the .NET API, Qdrant and the v1 stack together
# fit, but the image builds peak above what is free. Swap is a safety net (low swappiness), not extra speed.
set -e
if swapon --show | grep -q .; then
    echo "Swap is already active:"
    swapon --show
    exit 0
fi
fallocate -l 2G /swapfile
chmod 600 /swapfile
mkswap /swapfile
swapon /swapfile
grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
echo 'vm.swappiness=10' > /etc/sysctl.d/99-swappiness.conf
sysctl -w vm.swappiness=10
free -m
