#!/bin/bash
set -e

echo "=== Validating Terraform + Ansible Configuration ==="
echo ""

# Check prerequisites
echo "Checking prerequisites..."
command -v terraform >/dev/null 2>&1 || { echo "❌ Error: terraform is not installed"; exit 1; }
command -v ansible-playbook >/dev/null 2>&1 || { echo "❌ Error: ansible-playbook is not installed"; exit 1; }
echo "✅ Prerequisites OK"
echo ""

# Validate Terraform
echo "=== Validating Terraform ==="
cd terraform
echo "Formatting Terraform files..."
terraform fmt
echo "Validating Terraform configuration..."
if terraform validate; then
    echo "✅ Terraform configuration is valid"
else
    echo "❌ Terraform validation failed"
    exit 1
fi
echo ""

# Validate Ansible
echo "=== Validating Ansible ==="
cd ../ansible
echo "Checking Ansible inventory..."
if ansible-inventory --list >/dev/null 2>&1; then
    echo "✅ Ansible inventory is valid"
else
    echo "❌ Ansible inventory has errors"
    exit 1
fi

echo "Checking Ansible playbooks syntax..."
for playbook in playbooks/*.yml site.yml; do
    if [ -f "$playbook" ]; then
        if ansible-playbook --syntax-check "$playbook" >/dev/null 2>&1; then
            echo "✅ $playbook"
        else
            echo "❌ $playbook has syntax errors"
            ansible-playbook --syntax-check "$playbook"
            exit 1
        fi
    fi
done

echo ""
echo "=== All Validations Passed ==="
echo ""
echo "Configuration is ready to deploy!"
echo "Next steps:"
echo "1. Configure terraform/terraform.tfvars with Proxmox credentials"
echo "2. Run: ./deploy.sh"
echo "   Or manually:"
echo "   cd terraform && terraform apply"
echo "   cd ../ansible && ansible-playbook site.yml"
