#!/usr/bin/env python3
"""Test script to verify LXC config changes"""
import yaml
from libs.config import LabConfig

with open('enva.yaml') as f:
    data = yaml.safe_load(f)

cfg = LabConfig.from_dict(data, environment='test')
print(f'LXC host: {cfg.lxc_host}')
print(f'LXC storage: {cfg.lxc_storage}')
print(f'LXC bridge: {cfg.lxc_bridge}')
print(f'LXC template_dir: {cfg.lxc_template_dir}')
print(f'\nBackward compatibility (deprecated):')
print(f'proxmox_host: {cfg.proxmox_host}')
print(f'proxmox_storage: {cfg.proxmox_storage}')
print(f'proxmox_bridge: {cfg.proxmox_bridge}')
print(f'proxmox_template_dir: {cfg.proxmox_template_dir}')
print('\n✓ Config loaded successfully!')
