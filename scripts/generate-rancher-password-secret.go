package main

import (
	"crypto/rand"
	"crypto/sha3"
	"encoding/base64"
	"flag"
	"fmt"
	"hash"
	"os"

	"golang.org/x/crypto/pbkdf2"
)

const (
	iterations = 210000
	keyLength  = 32
	saltLength = 32
)

func main() {
	var password string
	var username string
	var namespace string

	flag.StringVar(&password, "password", "", "Password to hash (required)")
	flag.StringVar(&username, "username", "", "Username (User resource name, e.g., user-84slx) (required)")
	flag.StringVar(&namespace, "namespace", "cattle-local-user-passwords", "Namespace for the secret")
	flag.Parse()

	if password == "" {
		fmt.Fprintf(os.Stderr, "Error: password is required\n")
		flag.Usage()
		os.Exit(1)
	}

	if username == "" {
		fmt.Fprintf(os.Stderr, "Error: username is required\n")
		flag.Usage()
		os.Exit(1)
	}

	// Generate random salt
	salt := make([]byte, saltLength)
	if _, err := rand.Read(salt); err != nil {
		fmt.Fprintf(os.Stderr, "Error generating salt: %v\n", err)
		os.Exit(1)
	}

	// Hash password using PBKDF2-SHA3-512 (same as Rancher)
	// Note: sha3.New512 returns *sha3.SHA3 which implements hash.Hash
	// We wrap it to match the func() hash.Hash signature required by pbkdf2.Key
	hashedPassword := pbkdf2.Key([]byte(password), salt, iterations, keyLength, func() hash.Hash { return sha3.New512() })

	// Base64 encode
	passwordB64 := base64.StdEncoding.EncodeToString(hashedPassword)
	saltB64 := base64.StdEncoding.EncodeToString(salt)

	// Generate Kubernetes Secret YAML
	secretYAML := fmt.Sprintf(`apiVersion: v1
kind: Secret
metadata:
  name: %s
  namespace: %s
  annotations:
    cattle.io/password-hash: pbkdf2sha3512
type: Opaque
data:
  password: %s
  salt: %s
`, username, namespace, passwordB64, saltB64)

	fmt.Print(secretYAML)
}
