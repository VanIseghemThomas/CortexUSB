#!/bin/sh
# Get current date in YY.MM format
CALVER=$(date +'%y.%m')

# Get total commit count for build number
BUILD_NUMBER=$(git rev-list --count HEAD)

# Create full version
VERSION="${CALVER}.${BUILD_NUMBER}"

# Short SHA for tagging
SHORT_SHA=$(git rev-parse --short HEAD)

# Only write to GITHUB_OUTPUT if in GitHub Actions environment
if [ -n "$GITHUB_OUTPUT" ]; then
    echo "version=${VERSION}" >> $GITHUB_OUTPUT
    echo "build_number=${BUILD_NUMBER}" >> $GITHUB_OUTPUT
    echo "short_sha=${SHORT_SHA}" >> $GITHUB_OUTPUT
    echo "full_version=${VERSION}+${SHORT_SHA}" >> $GITHUB_OUTPUT
else
    export VERSION="$VERSION"
    export BUILD_NUMBER="$BUILD_NUMBER"
    export SHORT_SHA="$SHORT_SHA"
    export FULL_VERSION="${VERSION}+${SHORT_SHA}"
fi

echo "Generated version: ${VERSION} (${SHORT_SHA})"