#!/usr/bin/env bash

set -e -u -o pipefail -x # TODO remove -x from here
if [[ -n "${DEBUG-}" ]]; then
    set -x
fi

pushd "$(dirname "$0")"
    TEST_RESULTS_DIRECTORY="${1?Please enter the directory to the test results.}"
    TARGET_PLATFORM="${2?Please enter the target platform.}"

    BUILDKITE_ARTIFACT_PATH="${TEST_RESULTS_DIRECTORY}"
    UPLOAD_ARTIFACT_PATH="https://buildkite.com/organizations/${BUILDKITE_ORGANIZATION_SLUG}/pipelines/${BUILDKITE_PIPELINE_SLUG}/builds/${BUILDKITE_BUILD_ID}/jobs/${BUILDKITE_JOB_ID}/artifacts"
    TEST_RESULTS_FILE="${TEST_RESULTS_DIRECTORY}/index.html"

    if [[ -f "${TEST_RESULTS_FILE}" ]]; then
        # The Unreal Engine produces a mostly undocumented index.html/index.json as the result of running a test suite, for now seems mostly
        # for internal use - but it's an okay visualisation for test results, so we fix it up here to display as a build artifact in CI
        # (replacing local dependencies in the html by CDNs or correcting paths)
        sed 's?/bower_components/font-awesome/css/font-awesome.min.css?https://cdnjs.cloudflare.com/ajax/libs/font-awesome/4.7.0/css/font-awesome.min.css?g'                "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/twentytwenty/css/twentytwenty.css?https://cdnjs.cloudflare.com/ajax/libs/mhayes-twentytwenty/1.0.0/css/twentytwenty.min.css?g'             "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/featherlight/release/featherlight.min.css?https://cdnjs.cloudflare.com/ajax/libs/featherlight/1.7.13/featherlight.min.css?g'               "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/bootstrap/dist/css/bootstrap.min.css?https://cdnjs.cloudflare.com/ajax/libs/twitter-bootstrap/3.3.7/css/bootstrap.min.css?g'               "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/jquery/dist/jquery.min.js?https://cdnjs.cloudflare.com/ajax/libs/jquery/3.1.1/jquery.min.js?g'                                             "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/jquery.event.move/js/jquery.event.move.js?https://cdnjs.cloudflare.com/ajax/libs/mhayes-twentytwenty/1.0.0/js/jquery.event.move.min.js?g'  "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/jquery_lazyload/jquery.lazyload.js?https://cdnjs.cloudflare.com/ajax/libs/jquery_lazyload/1.9.7/jquery.lazyload.min.js?g'                  "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/twentytwenty/js/jquery.twentytwenty.js?https://cdnjs.cloudflare.com/ajax/libs/mhayes-twentytwenty/1.0.0/js/jquery.twentytwenty.min.js?g'   "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/clipboard/dist/clipboard.min.js?https://cdnjs.cloudflare.com/ajax/libs/clipboard.js/1.5.16/clipboard.min.js?g'                             "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/anchor-js/anchor.min.js?https://cdnjs.cloudflare.com/ajax/libs/anchor-js/3.2.2/anchor.min.js?g'                                            "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/featherlight/release/featherlight.min.js?https://cdnjs.cloudflare.com/ajax/libs/featherlight/1.7.13/featherlight.min.js?g'                 "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/bootstrap/dist/js/bootstrap.min.js?https://cdnjs.cloudflare.com/ajax/libs/twitter-bootstrap/3.3.7/js/bootstrap.min.js?g'                   "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/dustjs-linkedin/dist/dust-full.min.js?https://cdnjs.cloudflare.com/ajax/libs/dustjs-linkedin/2.7.5/dust-full.min.js?g'                     "${TEST_RESULTS_FILE}"
        sed 's?/bower_components/numeral/min/numeral.min.js?https://cdnjs.cloudflare.com/ajax/libs/numeral.js/2.0.4/numeral.min.js?g'                                       "${TEST_RESULTS_FILE}"

        "Test results in a nicer format can be found <a href='artifact://${TEST_RESULTS_FILE}'>here</a>."| buildkite-agent annotate
            --context "unreal-gdk-test-artifact-location"
            --style info

    else
        echo "The Unreal Editor crashed while running tests, see the test-gdk annotation for logs (or the tests.log buildkite artifact)."
        exit 1
    fi

    # Upload artifacts to Buildkite, capture output to extract artifact ID in the Slack message generation
    UPLOAD_OUTPUT=$(buildkite-agent artifact upload "${TEST_RESULTS_DIRECTORY}")
    if [ $? -neq 0 ]; then
        echo "Failed to upload artifact."
        exit 1
    fi

    # Artifacts are assigned an ID upon upload, so grab IDs from upload process output to build the artifact URLs
    # The output log is: "Uploading artifact <artifact id> <upload path>". We are interested in the artifact id
    REGEX="[^ ]* ${TEST_RESULTS_DIRECTORY}/index.html.*[^ ]* ${TEST_RESULTS_DIRECTORY}/tests.log"
    if [[ ${UPLOAD_OUTPUT} =~ ${REGEX} ]]; then
        TEST_RESULTS_URL="${UPLOAD_ARTIFACT_PATH}/${BASH_REMATCH[1]}"
        TEST_LOG_URL="${UPLOAD_ARTIFACT_PATH}/${BASH_REMATCH[2]}"
    else
        echo "Failed to extract artifact ids"
        exit 1
    fi

    # Read the test results
    RESULTS_PATH="${TEST_RESULTS_DIRECTORY}/index.json"
    SLACK_ATTACHMENT_FILE="${TEST_RESULTS_DIRECTORY}\slack_attachment_${BUILDKITE_STEP_ID}.json"
    TESTS_SUMMARY_FILE="${TEST_RESULTS_DIRECTORY}\test_summary_${BUILDKITE_STEP_ID}.json"

    TESTS_PASSED="good"
    if [[ $(cat ${RESULTS_PATH} | jq '.failed') == 0 ]]; then
        TESTS_PASSED="danger"
    fi

    TOTAL_TESTS_SUCCEEDED=$(($(cat ${RESULTS_PATH} | jq '.succeeded') + $(cat ernst.json | jq '.succeededWithWarnings')))
    TOTAL_TESTS_RUN=$(($(cat ${RESULTS_PATH} | jq '.failed') + ${TOTAL_TESTS_SUCCEEDED=}))
    jq \
        --arg value0 "Find the test results at ${test_results_url}" \
        --arg value1 "${TESTS_PASSED}" \
        --arg value2  "*${ENGINE_COMMIT_HASH}* $(basename ${TEST_RESULTS_DIRECTORY})" \
        --arg value3  "Passed ${TOTAL_TESTS_SUCCEEDED} / ${TOTAL_TESTS_RUN} tests." \
        --arg value4  "${TEST_RESULTS_URL}" \
        --arg value5  "${TEST_LOG_URL}" \
       '{
           fallback: $value0,
           color: $value1,
           fields: [
                {
                   value: $value2,
                   short: true
                },
                {
                   value: $value3,
                   short: true
                },
           ],
           actions: [
                {
                    "url": $value4,
                    "style": "primary",
                    "type": "button",
                    "text": ":bar_chart: Test results"
                },
                {
                    "url": $value5,
                    "style": "primary",
                    "type": "button",
                    "text": ":page_with_curl: Test log"
                },
           ]
        }' >> "${SLACK_ATTACHMENT_FILE}"

    buildkite-agent artifact upload "${SLACK_ATTACHMENT_FILE}"

    # Count the number of SpatialGDK tests in order to report this
    NUM_GDK_TESTS=0
    # Foreach ($test in $test_results_obj.tests) {
    #     if ($test.fulltestPath.Contains("SpatialGDK.")) {
    #         $num_gdk_tests += 1
    #     }
    # }
    # Count the number of Project (functional) tests in order to report this
    NUM_PROJECT_TESTS=0
    # Foreach ($test in $test_results_obj.tests) {
    #     if ($test.fulltestPath.Contains("Project.")) {
    #         $num_project_tests += 1
    #     }
    # }

    jq \
        --arg value0 $(date +%s) \
        --arg value1 "${BUILDKITE_BUILD_URL}" \
        --arg value2  "${TARGET_PLATFORM}" \
        --arg value3  "${ENGINE_COMMIT_HASH}" \
        --arg value4  "${TESTS_PASSED}" \
        --arg value5  "$(cat ${RESULTS_PATH} | jq '.totalDuration')" \
        --arg value6  "${TOTAL_TESTS_RUN}" \
        --arg value7  "${NUM_GDK_TESTS}" \
        --arg value8  "${NUM_PROJECT_TESTS}" \
        --arg value9  "$(basename ${TEST_RESULTS_DIRECTORY})" \
       '{
           time: $value0,
           build_url: $value1,
           platform: $value2,
           unreal_engine_commit: $value3,
           passed_all_tests: $value4,
           tests_duration_seconds: $value5,
           num_tests: $value6,
           num_gdk_tests: $value7,
            num_project_tests: $value8,
           test_result_directory_name: $value9,
        }' >> "${TESTS_SUMMARY_FILE}"
    buildkite-agent artifact upload "${TESTS_SUMMARY_FILE}"

    # Fail this build if any tests failed
    if [[ ${TESTS_PASSED} -eq 0 ]]; then
        echo "Tests failed. Logs for these tests are contained in the tests.log artifact."
        exit 1
    fi
    echo "All tests passed!"
popd
